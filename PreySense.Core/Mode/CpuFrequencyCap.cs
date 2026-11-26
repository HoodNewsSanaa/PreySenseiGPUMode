using System;

namespace PreySense.Mode
{
    /// <summary>
    /// iGPU Only fork (experimental): Windows' "maximum processor frequency" (PROCFREQMAX for every core class),
    /// held on the active power plan by <see cref="PowerPlanGuard"/> so Windows can't quietly undo it.
    /// Intel's own power service (DTT/IPF) can still lower clocks further; it cannot raise them past this cap.
    /// </summary>
    public static class CpuFrequencyCap
    {
        private static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>PROCFREQMAX (class 0), PROCFREQMAX1 (class 1) and PROCFREQMAX2 (class 2, newer Windows only).</summary>
        private static readonly PowerPlanSetting[] Settings =
        {
            new(SubgroupProcessor, new("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), "Max CPU frequency"),
            new(SubgroupProcessor, new("75b0ae3f-bce0-45a7-8c89-c9611c25e101"), "Max CPU frequency (class 1)"),
            new(SubgroupProcessor, new("75b0ae3f-bce0-45a7-8c89-c9611c25e102"), "Max CPU frequency (class 2)"),
        };

        public const int MinimumCapMhz = 800;
        public const int MaximumCapMhz = 6500;

        public static bool IsValidCap(int? mhz) => mhz is >= MinimumCapMhz and <= MaximumCapMhz;

        /// <summary>Writes <paramref name="mhz"/> (0 = no cap) for AC and DC; a cap above 0 is held until the next call.</summary>
        public static bool Apply(int mhz)
        {
            uint value = mhz <= 0 ? 0u : (uint)Math.Clamp(mhz, MinimumCapMhz, MaximumCapMhz);
            bool ok = true;
            for (int i = 0; i < Settings.Length; i++)
            {
                bool required = i < 2; // class 2 only exists on newer Windows builds
                ok &= value == 0
                    ? PowerPlanGuard.Release(Settings[i], 0, 0, required)
                    : PowerPlanGuard.Hold(Settings[i], value, required);
            }
            return ok;
        }

        /// <summary>Removes the cap (0 = unlimited) and stops holding it.</summary>
        public static bool Clear() => Apply(0);

        /// <summary>Reads the class-1 (performance core) cap on the active plan in MHz; 0 = no cap.</summary>
        public static int? ReadActiveCapMhz(bool onBattery = false) =>
            PowerPlanGuard.Read(Settings[1], onBattery) is uint v ? (int)v : null;
    }
}
