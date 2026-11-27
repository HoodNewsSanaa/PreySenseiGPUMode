using System;
using System.Threading;
using PreySense.Helpers;

namespace PreySense.Gpu
{
    /// <summary>
    /// iGPU Only fork: per-mode maximum RTX clock (the same as <c>nvidia-smi -lgc</c>). Capping the clock also
    /// keeps the GPU off the high-voltage end of its curve, so it runs cooler and quieter.
    /// The cap is only sent while the RTX is already awake (D0): asking an asleep GPU would wake it. While it
    /// sleeps, the request waits and Windows' cached power state is checked every few seconds (no NVIDIA call).
    /// The driver keeps the cap through sleep; it is lost when the driver restarts (for example when iGPU Only
    /// switches the RTX back on), which is why <see cref="MarkDriverRestarted"/> asks for it again.
    /// </summary>
    public static class GpuClockLock
    {
        public const int MinimumMhz = 600;
        public const int MaximumMhz = 3500;
        private static readonly TimeSpan PendingCheckInterval = TimeSpan.FromSeconds(5);

        private static readonly object Gate = new();
        private static int _desiredMhz;     // 0 = no cap wanted
        private static bool _pending;       // a set or reset still has to reach the driver
        private static Timer? _timer;

        /// <summary>Test seam: the device power state reader (1 = D0 awake … 4 = D3).</summary>
        public static Func<int?> PowerStateReader { get; set; } = ReadPowerState;

        /// <summary>Test seam: true when the RTX is disabled (iGPU Only).</summary>
        public static Func<bool> DeviceDisabledReader { get; set; } = () => DeviceHelper.IsDisplayDeviceDisabled("VEN_10DE") == true;

        /// <summary>Test seam: sends a cap (MHz) or, for 0, removes it. Returns the NVML status.</summary>
        public static Func<int, int> DriverWriter { get; set; } = WriteToDriver;

        public static bool IsValid(int? mhz) => mhz is >= MinimumMhz and <= MaximumMhz;

        /// <summary>The cap currently wanted (0 = none).</summary>
        public static int DesiredMhz { get { lock (Gate) return _desiredMhz; } }

        /// <summary>True while a set or reset is waiting for the RTX to wake up.</summary>
        public static bool IsPending { get { lock (Gate) return _pending; } }

        /// <summary>
        /// Asks for a cap (<paramref name="maxMhz"/> &gt; 0) or for no cap (0). When <paramref name="maxMhz"/> is 0 the
        /// driver is only told to reset if <paramref name="resetDriver"/> is true (a cap PreySense set may be in place).
        /// Returns true when the request reached the driver now, false when it is waiting or failed.
        /// </summary>
        public static bool Request(int maxMhz, bool resetDriver)
        {
            lock (Gate)
            {
                _desiredMhz = IsValid(maxMhz) ? maxMhz : 0;
                _pending = _desiredMhz > 0 || resetDriver;
                return TryApplyLocked();
            }
        }

        /// <summary>Call after the NVIDIA driver restarts (the RTX was switched back on): the cap must be sent again.</summary>
        public static void MarkDriverRestarted()
        {
            lock (Gate)
            {
                if (_desiredMhz == 0) return;
                _pending = true;
                TryApplyLocked();
            }
        }

        /// <summary>Stops waiting for the RTX (used on exit). The driver keeps any cap already sent until reboot.</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                _pending = false;
                StopTimerLocked();
            }
        }

        private static bool TryApplyLocked()
        {
            if (!_pending)
            {
                StopTimerLocked();
                return false;
            }
            int? state;
            try { state = PowerStateReader(); }
            catch { state = null; }
            if (state != 1)
            {
                bool disabled;
                try { disabled = state is null && DeviceDisabledReader(); }
                catch { disabled = false; }
                // Disabled (iGPU Only): nothing to watch; MarkDriverRestarted sends it when the RTX comes back.
                // Asleep: check Windows' cached state every few seconds without waking it.
                if (disabled) StopTimerLocked();
                else StartTimerLocked();
                return false;
            }

            // Never at the same time as a GPU mode switch or an RTX restart (MarkDriverRestarted is called from
            // inside one): retry on the next timer tick instead.
            using var nvidiaLease = PreySense.Core.Services.HardwareOperationGate.TryEnterNvidiaWork(TimeSpan.Zero);
            if (nvidiaLease is null)
            {
                StartTimerLocked();
                return false;
            }
            int rc = DriverWriter(_desiredMhz);
            if (rc == 0)
            {
                AppLogger.Log(_desiredMhz > 0
                    ? $"[GPU clock] Max RTX clock set to {_desiredMhz} MHz."
                    : "[GPU clock] Max RTX clock limit removed.");
                _pending = false;
                StopTimerLocked();
                return true;
            }
            if (rc is NvmlHelper.NVML_ERROR_NOT_SUPPORTED or NvmlHelper.NVML_ERROR_NO_PERMISSION)
            {
                AppLogger.Log($"[GPU clock] The NVIDIA driver refused the clock limit (NVML {rc}); not retrying.");
                _pending = false;
                StopTimerLocked();
                return false;
            }
            // NVML not ready yet (driver starting, GPU just woke): try again shortly.
            StartTimerLocked();
            return false;
        }

        private static void OnTimer()
        {
            lock (Gate) TryApplyLocked();
        }

        private static void StartTimerLocked()
        {
            _timer ??= new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(PendingCheckInterval, PendingCheckInterval);
        }

        private static void StopTimerLocked()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private static int? ReadPowerState()
        {
            DeviceHelper.InvalidateDisplayDevicePowerStateCache();
            return DeviceHelper.GetDisplayDevicePowerState("VEN_10DE");
        }

        private static int WriteToDriver(int maxMhz)
        {
            int rc = maxMhz > 0
                ? NvmlHelper.SetGpuLockedClocks((uint)maxMhz)
                : NvmlHelper.ResetGpuLockedClocks();
            // Don't keep NVML open: telemetry releases it at idle so the RTX can go back to sleep.
            NvmlHelper.Shutdown();
            return rc;
        }
    }
}
