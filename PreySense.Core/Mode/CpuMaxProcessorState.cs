using System;

namespace PreySense.Mode
{
    /// <summary>
    /// iGPU Only fork: Windows' "Maximum processor state" (PROCTHROTTLEMAX), as a percentage, held on the active
    /// power plan by <see cref="PowerPlanGuard"/>. On hybrid Intel chips Windows keeps one value for efficiency
    /// class 0 (E-cores) and one for class 1 (P-cores). 99% is the well-known way to turn Turbo Boost off;
    /// lower values cap further, but how a percentage maps to a clock speed is up to Windows and the CPU.
    /// </summary>
    public static class CpuMaxProcessorState
    {
        private static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>PROCTHROTTLEMAX1: efficiency class 1, the P-cores on Intel hybrid chips.</summary>
        public static readonly PowerPlanSetting PerformanceCores = new(
            SubgroupProcessor, new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"), "Max processor state (P-cores)");

        /// <summary>PROCTHROTTLEMAX: efficiency class 0, the E-cores on Intel hybrid chips (all cores elsewhere).</summary>
        public static readonly PowerPlanSetting EfficientCores = new(
            SubgroupProcessor, new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), "Max processor state (E-cores)");

        public const int MinimumPercent = 5;
        public const int MaximumPercent = 100;

        public static bool IsValid(int? percent) => percent is >= MinimumPercent and <= MaximumPercent;

        /// <summary>Writes <paramref name="percent"/> for AC and DC and holds it until <see cref="Restore"/>.</summary>
        public static bool Apply(PowerPlanSetting setting, int percent) =>
            PowerPlanGuard.Hold(setting, (uint)Math.Clamp(percent, MinimumPercent, MaximumPercent));

        /// <summary>Stops holding the setting and writes back the values it had before PreySense changed it.</summary>
        public static bool Restore(PowerPlanSetting setting, int? ac, int? dc) =>
            PowerPlanGuard.Release(setting, ac is int a ? (uint)a : null, dc is int d ? (uint)d : null);

        /// <summary>The current AC or DC value on the active plan.</summary>
        public static int? Read(PowerPlanSetting setting, bool dc) =>
            PowerPlanGuard.Read(setting, dc) is uint v ? (int)v : null;
    }
}
