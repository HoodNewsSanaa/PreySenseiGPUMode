using System;

namespace PreySense.Mode
{
    /// <summary>
    /// iGPU Only fork: Windows processor power settings PreySense can set per mode, held on the active power plan by
    /// <see cref="PowerPlanGuard"/>.
    /// <list type="bullet">
    /// <item>Energy performance preference (PERFEPP / PERFEPP1): 0 = all performance, 100 = all efficiency. Tells the
    /// CPU how eagerly to raise its clocks. Class 0 is the E-cores and class 1 the P-cores on Intel hybrid chips.</item>
    /// <item>Heterogeneous scheduling policy (SCHEDPOLICY for long-running threads, SHORTSCHEDPOLICY for short ones):
    /// 0 all processors, 1 performant only, 2 prefer performant, 3 efficient only, 4 prefer efficient, 5 automatic.</item>
    /// </list>
    /// </summary>
    public static class ProcessorPolicySettings
    {
        private static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        public static readonly PowerPlanSetting EppEfficientCores = new(
            SubgroupProcessor, new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), "Energy performance preference (E-cores)");

        public static readonly PowerPlanSetting EppPerformanceCores = new(
            SubgroupProcessor, new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), "Energy performance preference (P-cores)");

        public static readonly PowerPlanSetting LongThreadScheduling = new(
            SubgroupProcessor, new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d"), "Scheduling policy (long threads)");

        public static readonly PowerPlanSetting ShortThreadScheduling = new(
            SubgroupProcessor, new Guid("bae08b81-2d5e-4688-ad6a-13243356654b"), "Scheduling policy (short threads)");

        public const int SchedulingEfficientOnly = 3;
        public const int SchedulingPreferEfficient = 4;

        public static bool IsValidEpp(int? percent) => percent is >= 0 and <= 100;

        /// <summary>The per-mode choices offered: prefer E-cores, or E-cores only.</summary>
        public static bool IsValidScheduling(int? policy) => policy is SchedulingEfficientOnly or SchedulingPreferEfficient;

        /// <summary>Writes <paramref name="value"/> for AC and DC and holds it until <see cref="Restore"/>.</summary>
        public static bool Apply(PowerPlanSetting setting, int value) => PowerPlanGuard.Hold(setting, (uint)Math.Max(0, value));

        /// <summary>Stops holding the setting and writes back the values it had before PreySense changed it.</summary>
        public static bool Restore(PowerPlanSetting setting, int? ac, int? dc) =>
            PowerPlanGuard.Release(setting, ac is int a ? (uint)a : null, dc is int d ? (uint)d : null);

        /// <summary>The current AC or DC value on the active plan.</summary>
        public static int? Read(PowerPlanSetting setting, bool dc) =>
            PowerPlanGuard.Read(setting, dc) is uint v ? (int)v : null;
    }
}
