using System;

namespace PreySense.Mode
{
    /// <summary>
    /// iGPU Only fork: Windows' "PCI Express → Link State Power Management" (ASPM): 0 = Off, 1 = Moderate,
    /// 2 = Maximum power savings. Lets PCIe links (SSD, Wi-Fi, …) drop into low-power states, which the
    /// processor needs before it can reach its deeper package sleep states.
    /// </summary>
    public static class PcieLinkPowerSaving
    {
        public static readonly PowerPlanSetting Setting = new(
            new Guid("501a4d13-42af-4429-9fd1-a8218c268e20"),
            new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5"),
            "PCIe link power saving");

        public const int Off = 0;
        public const int Moderate = 1;
        public const int Maximum = 2;

        public static bool IsValid(int? level) => level is >= Off and <= Maximum;

        /// <summary>Writes <paramref name="level"/> for AC and DC and holds it until <see cref="Restore"/>.</summary>
        public static bool Apply(int level) => PowerPlanGuard.Hold(Setting, (uint)Math.Clamp(level, Off, Maximum));

        /// <summary>Stops holding the setting and writes back the values it had before PreySense changed it.</summary>
        public static bool Restore(int? ac, int? dc) =>
            PowerPlanGuard.Release(Setting, ac is int a ? (uint)a : null, dc is int d ? (uint)d : null);

        /// <summary>The current AC or DC value on the active plan.</summary>
        public static int? Read(bool dc) => PowerPlanGuard.Read(Setting, dc) is uint v ? (int)v : null;
    }
}
