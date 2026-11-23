using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PreySense.Mode
{
    /// <summary>
    /// iGPU Only fork (experimental): writes Windows' "maximum processor frequency" settings
    /// (PROCFREQMAX for every core class) on the active power plan and its active overlay, and
    /// puts them back when something else rewrites them. Watching uses a registry change
    /// notification, so nothing runs while the values are left alone.
    /// Intel's own power service (DTT/IPF) can still lower clocks further; it cannot raise them past this cap.
    /// </summary>
    public static class CpuFrequencyCap
    {
        private static readonly object Gate = new();
        private static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>PROCFREQMAX (class 0), PROCFREQMAX1 (class 1) and PROCFREQMAX2 (class 2, newer Windows only).</summary>
        private static readonly Guid[] FrequencySettings =
        {
            new("75b0ae3f-bce0-45a7-8c89-c9611c25e100"),
            new("75b0ae3f-bce0-45a7-8c89-c9611c25e101"),
            new("75b0ae3f-bce0-45a7-8c89-c9611c25e102"),
        };

        public const int MinimumCapMhz = 800;
        public const int MaximumCapMhz = 6500;
        private const int MaxCorrectionsPerWindow = 20;
        private static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CorrectionDelay = TimeSpan.FromMilliseconds(750);

        private static uint _guardedMhz;
        private static RegistryKey? _watchedKey;
        private static AutoResetEvent? _changed;
        private static RegisteredWaitHandle? _wait;
        private static Timer? _correctionTimer;
        private static readonly Queue<DateTime> Corrections = new();

        /// <summary>The cap the guard is currently holding, or 0 when no cap is held.</summary>
        public static int GuardedMhz { get { lock (Gate) return (int)_guardedMhz; } }

        public static bool IsValidCap(int? mhz) => mhz is >= MinimumCapMhz and <= MaximumCapMhz;

        /// <summary>
        /// Writes <paramref name="mhz"/> (0 = no cap) to AC and DC for every core class and re-activates the plan.
        /// A cap above 0 is then held until <see cref="Clear"/> or another <see cref="Apply"/>.
        /// </summary>
        public static bool Apply(int mhz)
        {
            uint value = mhz <= 0 ? 0u : (uint)Math.Clamp(mhz, MinimumCapMhz, MaximumCapMhz);
            lock (Gate)
            {
                bool ok = WriteAll(value);
                if (value == 0) StopGuardLocked();
                else StartGuardLocked(value);
                return ok;
            }
        }

        /// <summary>Removes the cap (writes 0 = unlimited) and stops guarding it.</summary>
        public static bool Clear() => Apply(0);

        /// <summary>Reads the class-1 (performance core) cap on the active plan in MHz; 0 = no cap.</summary>
        public static int? ReadActiveCapMhz(bool onBattery = false)
        {
            if (!TryGetActiveScheme(out Guid scheme)) return null;
            Guid sub = SubgroupProcessor, setting = FrequencySettings[1];
            uint value;
            uint result = onBattery
                ? PowerReadDCValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out value)
                : PowerReadACValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out value);
            return result == 0 ? (int)value : null;
        }

        private static bool WriteAll(uint value)
        {
            if (!TryGetActiveScheme(out Guid scheme)) return false;
            bool ok = WriteScheme(scheme, value, required: true);
            if (TryGetActiveOverlay(out Guid overlay) && overlay != Guid.Empty && overlay != scheme)
                WriteScheme(overlay, value, required: false);
            return PowerSetActiveScheme(IntPtr.Zero, in scheme) == 0 && ok;
        }

        private static bool WriteScheme(Guid scheme, uint value, bool required)
        {
            bool ok = true;
            Guid sub = SubgroupProcessor;
            for (int i = 0; i < FrequencySettings.Length; i++)
            {
                Guid setting = FrequencySettings[i];
                bool written = PowerWriteACValueIndex(IntPtr.Zero, in scheme, in sub, in setting, value) == 0
                    & PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in sub, in setting, value) == 0;
                // Class 2 only exists on newer Windows builds; its absence is not a failure.
                if (!written && required && i < 2) ok = false;
            }
            return ok;
        }

        private static bool IsHeld(uint value)
        {
            if (!TryGetActiveScheme(out Guid scheme)) return true;
            Guid sub = SubgroupProcessor;
            for (int i = 0; i < 2; i++)
            {
                Guid setting = FrequencySettings[i];
                if (PowerReadACValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out uint ac) == 0 && ac != value) return false;
                if (PowerReadDCValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out uint dc) == 0 && dc != value) return false;
            }
            return true;
        }

        private static void StartGuardLocked(uint value)
        {
            _guardedMhz = value;
            if (_wait is not null) return;
            try
            {
                _watchedKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", writable: false);
                if (_watchedKey is null) return;
                _changed = new AutoResetEvent(false);
                if (!Arm()) { StopGuardLocked(); return; }
                _correctionTimer = new Timer(_ => Correct(), null, Timeout.Infinite, Timeout.Infinite);
                _wait = ThreadPool.RegisterWaitForSingleObject(_changed, (_, _) => OnRegistryChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[CPU cap] Could not watch the power plan: " + ex.Message);
                StopGuardLocked();
            }
        }

        private static bool Arm()
        {
            if (_watchedKey is null || _changed is null) return false;
            // One-shot notification; re-armed after every change. Thread-agnostic so the pool thread may exit.
            int result = RegNotifyChangeKeyValue(_watchedKey.Handle, true,
                RegNotifyChangeLastSet | RegNotifyThreadAgnostic, _changed.SafeWaitHandle, true);
            return result == 0;
        }

        private static void OnRegistryChanged()
        {
            lock (Gate)
            {
                if (_guardedMhz == 0 || !Arm()) return;
                // Several values change at once during a plan or mode switch; act once they settle.
                _correctionTimer?.Change(CorrectionDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void Correct()
        {
            lock (Gate)
            {
                uint value = _guardedMhz;
                if (value == 0 || IsHeld(value)) return;
                DateTime now = DateTime.UtcNow;
                while (Corrections.Count > 0 && now - Corrections.Peek() > CorrectionWindow) Corrections.Dequeue();
                if (Corrections.Count >= MaxCorrectionsPerWindow)
                {
                    AppLogger.Log("[CPU cap] Windows keeps resetting the frequency cap; stopped re-applying it until the next mode change.");
                    StopGuardLocked();
                    return;
                }
                Corrections.Enqueue(now);
                AppLogger.Log($"[CPU cap] The power plan changed the cap; restoring {value} MHz.");
                WriteAll(value);
            }
        }

        private static void StopGuardLocked()
        {
            _guardedMhz = 0;
            try { _wait?.Unregister(null); } catch { }
            _wait = null;
            try { _correctionTimer?.Dispose(); } catch { }
            _correctionTimer = null;
            try { _changed?.Dispose(); } catch { }
            _changed = null;
            try { _watchedKey?.Dispose(); } catch { }
            _watchedKey = null;
            Corrections.Clear();
        }

        private static bool TryGetActiveScheme(out Guid scheme)
        {
            scheme = Guid.Empty;
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeGuidPtr) != 0 || activeGuidPtr == IntPtr.Zero)
                return false;
            try
            {
                scheme = Marshal.PtrToStructure<Guid>(activeGuidPtr);
                return true;
            }
            finally { LocalFree(activeGuidPtr); }
        }

        private static bool TryGetActiveOverlay(out Guid overlay)
        {
            overlay = Guid.Empty;
            try { return PowerGetEffectiveOverlayScheme(out overlay) == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private const int RegNotifyChangeLastSet = 0x00000004;
        private const int RegNotifyThreadAgnostic = 0x10000000;

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool watchSubtree, int notifyFilter,
            SafeWaitHandle hEvent, bool asynchronous);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid,
            in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint acValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid,
            in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint dcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid,
            in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint acValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid,
            in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint dcValueIndex);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
