using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PreySense.Core.Hardware;

/// <summary>One reading for the Power Diagnostics window.</summary>
public sealed record PowerDiagnosticsSnapshot(
    bool? OnAcPower,
    int? BatteryPercent,
    double? BatteryDischargeWatts,
    double? BatteryChargeWatts,
    double? CpuPackageWatts,
    IReadOnlyList<PackageCStateResidency> PackageCStates,
    bool PackageCStatesSupported,
    int? DiscreteGpuPowerState,
    bool? DiscreteGpuDisabled,
    double? BatteryRemainingWh = null)
{
    /// <summary>Battery drain not explained by the CPU package (screen, RTX, SSD, Wi-Fi, board, losses).</summary>
    public double? RestOfSystemWatts => BatteryDischargeWatts is double battery && CpuPackageWatts is double cpu
        ? Math.Max(0, battery - cpu) : null;
}

/// <summary>Share of the last interval the CPU package spent in one package C-state.</summary>
public readonly record struct PackageCStateResidency(string Name, double? Percent);

/// <summary>
/// iGPU Only fork: reads battery drain, CPU package power, package C-state residency and the RTX
/// power state. Nothing here wakes the RTX: its state comes from Windows' cached device power data.
/// Intended to be sampled about once a second, and only while the diagnostics window is open.
/// </summary>
public sealed class PowerDiagnosticsSampler
{
    private const uint MsrTsc = 0x010;
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPackageEnergy = 0x611;

    /// <summary>Package C-state residency counters (they count at the TSC rate on these CPUs).</summary>
    private static readonly (string Name, uint Msr)[] PackageCStateMsrs =
    {
        ("PC2", 0x60D), ("PC3", 0x3F8), ("PC6", 0x3F9), ("PC8", 0x630), ("PC10", 0x632),
    };

    /// <summary>
    /// Intel family-6 models known to implement every counter above (Alder Lake, Raptor Lake,
    /// Meteor Lake, Arrow Lake, Lunar Lake). Reading an MSR a CPU does not have can crash the system,
    /// so other CPUs get no C-state readings.
    /// </summary>
    private static readonly HashSet<int> SupportedModels = new()
    {
        0x97, 0x9A, 0xB7, 0xBA, 0xBF, 0xAA, 0xAC, 0xBD, 0xC5, 0xC6,
    };

    private readonly ICpuDriverBackend? _backend;
    private readonly bool _cStatesSupported;
    private double? _energyUnitJoules;
    private ulong _lastEnergy;
    private long _lastEnergyTicks;
    private bool _haveEnergy;
    private ulong _lastTsc;
    private readonly ulong[] _lastCState = new ulong[PackageCStateMsrs.Length];
    private bool _haveCStates;

    public PowerDiagnosticsSampler(ICpuDriverBackend? backend)
    {
        _backend = backend is { IsInitialized: true } ? backend : null;
        _cStatesSupported = _backend is not null && IsSupportedIntelCpu();
    }

    public bool PackageCStatesSupported => _cStatesSupported;

    public PowerDiagnosticsSnapshot Sample()
    {
        var (onAc, percent) = ReadSystemPowerStatus();
        var (discharge, charge, wmiOnline, remainingWh) = ReadBatteryStatus();
        onAc ??= wmiOnline;
        double? package = ReadPackageWatts();
        var cStates = ReadPackageCStates();
        int? gpuState = null;
        bool? gpuDisabled = null;
        try
        {
            PreySense.Helpers.DeviceHelper.InvalidateDisplayDevicePowerStateCache();
            gpuState = PreySense.Helpers.DeviceHelper.GetDisplayDevicePowerState("VEN_10DE");
            if (gpuState is null) gpuDisabled = PreySense.Helpers.DeviceHelper.IsDisplayDeviceDisabled("VEN_10DE");
        }
        catch { }
        return new(onAc, percent, discharge, charge, package, cStates, _cStatesSupported, gpuState, gpuDisabled, remainingWh);
    }

    /// <summary>Human-readable name for a CM_POWER_DATA device state (1 = D0 … 4 = D3).</summary>
    public static string DescribeGpuState(int? state, bool? disabled) => state switch
    {
        1 => "D0 (awake)",
        2 => "D1",
        3 => "D2",
        4 => "D3 (asleep)",
        _ => disabled == true ? "Disabled" : "Not found",
    };

    private double? ReadPackageWatts()
    {
        if (_backend is null) return null;
        try
        {
            if (_energyUnitJoules is null)
            {
                if (!_backend.ReadMsr(MsrRaplPowerUnit, out ulong units)) return null;
                _energyUnitJoules = 1.0 / (1UL << (int)((units >> 8) & 0x1F));
            }
            if (!_backend.ReadMsr(MsrPackageEnergy, out ulong raw)) return null;
            ulong energy = raw & 0xFFFFFFFF;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double? watts = null;
            if (_haveEnergy)
            {
                double seconds = (now - _lastEnergyTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                ulong delta = energy >= _lastEnergy ? energy - _lastEnergy : energy + (1UL << 32) - _lastEnergy;
                if (seconds > 0.05) watts = delta * _energyUnitJoules.Value / seconds;
            }
            _lastEnergy = energy;
            _lastEnergyTicks = now;
            _haveEnergy = true;
            return watts is >= 0 and < 500 ? watts : null;
        }
        catch { return null; }
    }

    private IReadOnlyList<PackageCStateResidency> ReadPackageCStates()
    {
        var result = new PackageCStateResidency[PackageCStateMsrs.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new(PackageCStateMsrs[i].Name, null);
        if (!_cStatesSupported || _backend is null) return result;
        try
        {
            var current = new ulong[PackageCStateMsrs.Length];
            if (!_backend.ReadMsr(MsrTsc, out ulong tsc)) return result;
            for (int i = 0; i < current.Length; i++)
                if (!_backend.ReadMsr(PackageCStateMsrs[i].Msr, out current[i])) return result;
            if (_haveCStates && tsc > _lastTsc)
            {
                double elapsed = tsc - _lastTsc;
                for (int i = 0; i < current.Length; i++)
                {
                    double? percent = current[i] >= _lastCState[i]
                        ? Math.Clamp((current[i] - _lastCState[i]) / elapsed * 100.0, 0, 100) : null;
                    result[i] = new(PackageCStateMsrs[i].Name, percent);
                }
            }
            _lastTsc = tsc;
            Array.Copy(current, _lastCState, current.Length);
            _haveCStates = true;
        }
        catch { }
        return result;
    }

    private static bool IsSupportedIntelCpu()
    {
        try
        {
            if (!X86Base.IsSupported) return false;
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            bool intel = ebx == 0x756E6547 && edx == 0x49656E69 && ecx == 0x6C65746E; // "GenuineIntel"
            if (!intel) return false;
            int eax = X86Base.CpuId(1, 0).Eax;
            int family = (eax >> 8) & 0xF;
            int model = ((eax >> 4) & 0xF) | (((eax >> 16) & 0xF) << 4);
            return family == 6 && SupportedModels.Contains(model);
        }
        catch { return false; }
    }

    private static (double? Discharge, double? Charge, bool? Online, double? RemainingWh) ReadBatteryStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                "SELECT DischargeRate, ChargeRate, PowerOnline, RemainingCapacity FROM BatteryStatus");
            using var results = searcher.Get();
            double discharge = 0, charge = 0, remaining = 0;
            bool any = false;
            bool? online = null;
            foreach (ManagementBaseObject battery in results)
            {
                using (battery)
                {
                    any = true;
                    // Some firmware reports the rate as a signed value; the sign is not meaningful here.
                    discharge += Math.Abs(Convert.ToDouble(battery["DischargeRate"] ?? 0));
                    charge += Math.Abs(Convert.ToDouble(battery["ChargeRate"] ?? 0));
                    if (battery["PowerOnline"] is bool isOnline) online = (online ?? false) || isOnline;
                    remaining += Convert.ToDouble(battery["RemainingCapacity"] ?? 0);
                }
            }
            if (!any) return (null, null, null, null);
            return (discharge / 1000.0, charge / 1000.0, online, remaining > 0 ? remaining / 1000.0 : null);
        }
        catch { return (null, null, null, null); }
    }

    private static (bool? OnAc, int? Percent) ReadSystemPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (null, null);
        bool? onAc = status.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        return (onAc, percent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
