using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PreySense.Mode
{
    /// <summary>A Windows power-plan setting: its subgroup and setting GUIDs.</summary>
    public readonly record struct PowerPlanSetting(Guid Subgroup, Guid Setting, string Name);

    /// <summary>
    /// iGPU Only fork: writes Windows power-plan settings (AC and DC, on the active plan and its active overlay)
    /// and holds them: if something else rewrites a held setting, it is put back. Watching uses a registry change
    /// notification, so nothing runs while the values are left alone.
    /// </summary>
    public static class PowerPlanGuard
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<PowerPlanSetting, uint> Held = new();
        private const int MaxCorrectionsPerWindow = 20;
        private static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CorrectionDelay = TimeSpan.FromMilliseconds(750);

        private static RegistryKey? _watchedKey;
        private static AutoResetEvent? _changed;
        private static RegisteredWaitHandle? _wait;
        private static Timer? _correctionTimer;
        private static readonly Queue<DateTime> Corrections = new();

        /// <summary>Writes <paramref name="value"/> for AC and DC and holds it until <see cref="Release"/>.</summary>
        public static bool Hold(PowerPlanSetting setting, uint value, bool required = true)
        {
            lock (Gate)
            {
                bool ok = WriteLocked(setting, value, value, required);
                Held[setting] = value;
                StartWatchLocked();
                return ok;
            }
        }

        /// <summary>Stops holding <paramref name="setting"/> and writes the given AC/DC values (when supplied).</summary>
        public static bool Release(PowerPlanSetting setting, uint? acValue = null, uint? dcValue = null, bool required = true)
        {
            lock (Gate)
            {
                Held.Remove(setting);
                if (Held.Count == 0) StopWatchLocked();
                if (acValue is null && dcValue is null) return true;
                return WriteLocked(setting, acValue, dcValue, required);
            }
        }

        /// <summary>The value currently held for <paramref name="setting"/>, if any.</summary>
        public static uint? HeldValue(PowerPlanSetting setting)
        {
            lock (Gate) return Held.TryGetValue(setting, out uint v) ? v : null;
        }

        /// <summary>Reads the setting's AC or DC value on the active plan.</summary>
        public static uint? Read(PowerPlanSetting setting, bool dc)
        {
            if (!TryGetActiveScheme(out Guid scheme)) return null;
            Guid sub = setting.Subgroup, id = setting.Setting;
            uint value;
            uint rc = dc
                ? PowerReadDCValueIndex(IntPtr.Zero, in scheme, in sub, in id, out value)
                : PowerReadACValueIndex(IntPtr.Zero, in scheme, in sub, in id, out value);
            return rc == 0 ? value : null;
        }

        private static bool WriteLocked(PowerPlanSetting setting, uint? ac, uint? dc, bool required)
        {
            if (!TryGetActiveScheme(out Guid scheme)) return false;
            bool ok = WriteScheme(scheme, setting, ac, dc) || !required;
            if (TryGetActiveOverlay(out Guid overlay) && overlay != Guid.Empty && overlay != scheme)
                WriteScheme(overlay, setting, ac, dc);
            return PowerSetActiveScheme(IntPtr.Zero, in scheme) == 0 && ok;
        }

        private static bool WriteScheme(Guid scheme, PowerPlanSetting setting, uint? ac, uint? dc)
        {
            Guid sub = setting.Subgroup, id = setting.Setting;
            bool ok = true;
            if (ac is uint a) ok &= PowerWriteACValueIndex(IntPtr.Zero, in scheme, in sub, in id, a) == 0;
            if (dc is uint d) ok &= PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in sub, in id, d) == 0;
            return ok;
        }

        private static bool AllHeldLocked()
        {
            foreach (var (setting, value) in Held)
            {
                // A setting this Windows build doesn't have reads as null: nothing to correct.
                if (Read(setting, dc: false) is uint ac && ac != value) return false;
                if (Read(setting, dc: true) is uint dc && dc != value) return false;
            }
            return true;
        }

        private static void StartWatchLocked()
        {
            if (_wait is not null) return;
            try
            {
                _watchedKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", writable: false);
                if (_watchedKey is null) return;
                _changed = new AutoResetEvent(false);
                if (!Arm()) { StopWatchLocked(); return; }
                _correctionTimer = new Timer(_ => Correct(), null, Timeout.Infinite, Timeout.Infinite);
                _wait = ThreadPool.RegisterWaitForSingleObject(_changed, (_, _) => OnRegistryChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
            }
            catch (Exception ex)
            {
                AppLogger.Log("[Power plan] Could not watch the power plan: " + ex.Message);
                StopWatchLocked();
            }
        }

        private static bool Arm()
        {
            if (_watchedKey is null || _changed is null) return false;
            // One-shot notification; re-armed after every change. Thread-agnostic so the pool thread may exit.
            return RegNotifyChangeKeyValue(_watchedKey.Handle, true,
                RegNotifyChangeLastSet | RegNotifyThreadAgnostic, _changed.SafeWaitHandle, true) == 0;
        }

        private static void OnRegistryChanged()
        {
            lock (Gate)
            {
                if (Held.Count == 0 || !Arm()) return;
                // Several values change at once during a plan or mode switch; act once they settle.
                _correctionTimer?.Change(CorrectionDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void Correct()
        {
            lock (Gate)
            {
                if (Held.Count == 0 || AllHeldLocked()) return;
                DateTime now = DateTime.UtcNow;
                while (Corrections.Count > 0 && now - Corrections.Peek() > CorrectionWindow) Corrections.Dequeue();
                if (Corrections.Count >= MaxCorrectionsPerWindow)
                {
                    AppLogger.Log("[Power plan] Windows keeps resetting PreySense's power-plan settings; stopped re-applying them until the next mode change.");
                    Held.Clear();
                    StopWatchLocked();
                    return;
                }
                Corrections.Enqueue(now);
                foreach (var (setting, value) in Held)
                {
                    AppLogger.Log($"[Power plan] {setting.Name} was changed; restoring {value}.");
                    WriteLocked(setting, value, value, required: false);
                }
            }
        }

        private static void StopWatchLocked()
        {
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
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return false;
            try { scheme = Marshal.PtrToStructure<Guid>(ptr); return true; }
            finally { LocalFree(ptr); }
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
