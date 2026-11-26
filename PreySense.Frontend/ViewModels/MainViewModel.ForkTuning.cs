using System;
using System.Threading.Tasks;
using PreySense.Core.Configuration;
using PreySense.Core.Hardware;
using PreySense.Gpu;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: per-mode PROCHOT override and per-mode CPU frequency cap (experimental).
/// Both are stored on the mode's tuning profile and applied whenever that mode is selected.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Test seam for the Windows frequency-cap writer (MHz, 0 = no cap).</summary>
    internal Func<int, bool>? CpuFrequencyCapWriter { get; set; }

    /// <summary>Test seam for the RTX clock limit: (MHz or 0, reset driver) → reached the driver now.</summary>
    internal Func<int, bool, bool>? GpuClockLockRequester { get; set; }

    /// <summary>Test seams for the PCIe link power setting: write a level, restore AC/DC, read AC (false) or DC (true).</summary>
    internal Func<int, bool>? PcieLinkPowerWriter { get; set; }
    internal Func<int?, int?, bool>? PcieLinkPowerRestorer { get; set; }
    internal Func<bool, int?>? PcieLinkPowerReader { get; set; }

    /// <summary>Test seams for the maximum processor state: write a percentage, restore AC/DC, read AC (false) or DC (true).</summary>
    internal Func<PowerPlanSetting, int, bool>? CpuMaxStateWriter { get; set; }
    internal Func<PowerPlanSetting, int?, int?, bool>? CpuMaxStateRestorer { get; set; }
    internal Func<PowerPlanSetting, bool, int?>? CpuMaxStateReader { get; set; }

    /// <summary>Test seams for NVIDIA's preferred GPU: read the current values, write one, restore saved values.</summary>
    internal Func<(NvidiaSettingsResult Result, NvidiaPreferredGpuValues Values)>? PreferredGpuReader { get; set; }
    internal Func<uint, NvidiaSettingsResult>? PreferredGpuWriter { get; set; }
    internal Func<NvidiaPreferredGpuValues, NvidiaSettingsResult>? PreferredGpuRestorer { get; set; }

    private static readonly object PreferredGpuGate = new();

    /// <summary>Test seam for the PROCHOT writer used when the edited mode is already active.</summary>
    internal Func<int, bool>? ProchotOffsetWriter { get; set; }

    /// <summary>TjMax for labelling PROCHOT choices: live reading, else the calibrated value.</summary>
    internal int? ModeExtrasTjMaxCelsius => ResolveTjMaxCelsius();

    internal int? ProchotOverrideForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).ProchotOffsetOverrideCelsius;

    internal int? CpuFrequencyCapForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).CpuMaxFrequencyMhz;

    internal int? GpuClockLimitForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).GpuMaxClockMhz;

    /// <summary>Saves a mode's RTX clock limit (null = no limit) and applies it if that mode is active.</summary>
    internal async Task SetGpuClockLimitForModeIndexAsync(int modeIndex, int? maxMhz)
    {
        if (!GpuClockLock.IsValid(maxMhz)) maxMhz = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).GpuMaxClockMhz = maxMhz);
        if (mode == ActivePowerMode)
            await Task.Run(() => ApplyGpuClockLockForMode(mode));
    }

    /// <summary>
    /// Asks for the mode's RTX clock limit, or removes a limit PreySense set earlier when the mode has none.
    /// While the RTX is asleep the request waits for it to wake up instead of waking it.
    /// </summary>
    internal void ApplyGpuClockLockForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            int? limit = ProfileForMode(tuning, mode).GpuMaxClockMhz;
            var request = GpuClockLockRequester ?? GpuClockLock.Request;
            if (GpuClockLock.IsValid(limit))
            {
                request(limit!.Value, false);
                if (tuning.LastAppliedGpuClockLockMhz != limit)
                    TuningStore.Update(cfg => cfg.Tuning.LastAppliedGpuClockLockMhz = limit);
            }
            else if (tuning.LastAppliedGpuClockLockMhz is not null)
            {
                request(0, true);
                TuningStore.Update(cfg => cfg.Tuning.LastAppliedGpuClockLockMhz = null);
            }
            else
            {
                request(0, false); // nothing of ours in place: just stop any pending request
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GPU clock] " + ex.Message);
        }
    }

    /// <summary>Saves a mode's PROCHOT override (null = use the global setting) and applies it if that mode is active.</summary>
    internal async Task<bool> SetProchotOverrideForModeIndexAsync(int modeIndex, int? offsetCelsius)
    {
        if (offsetCelsius is < 0 or > 63) offsetCelsius = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).ProchotOffsetOverrideCelsius = offsetCelsius);
        if (mode != ActivePowerMode || !IsHardwareTuningEnabled || IsProchotOffsetLocked) return true;
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        int? target = ResolveModeProchotOffset(tuning, ProfileForMode(tuning, mode));
        if (target is not int offset) return true;
        bool ok = await Task.Run(() =>
        {
            if (ProchotOffsetWriter is { } writer) return writer(offset);
            var backend = CpuDriverRuntime.Backend;
            if (backend is null) return false;
            return new CpuProchotController(backend).TrySetOffset(offset, out _);
        });
        if (!ok) DiagnosticMessage = "Could not apply the PROCHOT offset.";
        return ok;
    }

    /// <summary>Saves a mode's CPU frequency cap (null = no cap) and applies it if that mode is active.</summary>
    internal async Task SetCpuFrequencyCapForModeIndexAsync(int modeIndex, int? capMhz)
    {
        if (!CpuFrequencyCap.IsValidCap(capMhz)) capMhz = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).CpuMaxFrequencyMhz = capMhz);
        if (mode == ActivePowerMode)
            await Task.Run(() => ApplyCpuFrequencyCapForMode(mode));
    }

    /// <summary>
    /// Writes the mode's frequency cap, or removes a cap PreySense set earlier when the mode has none.
    /// A cap set by something else (for example by hand with powercfg) is left alone.
    /// </summary>
    internal void ApplyCpuFrequencyCapForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            int? cap = ProfileForMode(tuning, mode).CpuMaxFrequencyMhz;
            var writer = CpuFrequencyCapWriter ?? CpuFrequencyCap.Apply;
            if (CpuFrequencyCap.IsValidCap(cap))
            {
                if (writer(cap!.Value))
                {
                    if (tuning.LastAppliedCpuFrequencyCapMhz != cap)
                        TuningStore.Update(cfg => cfg.Tuning.LastAppliedCpuFrequencyCapMhz = cap);
                    AppLogger.Log($"[CPU cap] {GetModeTitle(mode)}: max CPU frequency {cap} MHz.");
                }
                else
                {
                    DiagnosticMessage = "Could not apply the CPU frequency cap.";
                    AppLogger.Error("[CPU cap] Windows refused the frequency cap.");
                }
            }
            else if (tuning.LastAppliedCpuFrequencyCapMhz is not null)
            {
                if (writer(0))
                {
                    TuningStore.Update(cfg => cfg.Tuning.LastAppliedCpuFrequencyCapMhz = null);
                    AppLogger.Log($"[CPU cap] {GetModeTitle(mode)}: frequency cap removed.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[CPU cap] " + ex.Message);
        }
    }

    internal int? PcieLinkPowerSavingForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).PcieLinkPowerSaving;

    /// <summary>Saves a mode's PCIe link power level (null = leave Windows' setting) and applies it if that mode is active.</summary>
    internal async Task SetPcieLinkPowerSavingForModeIndexAsync(int modeIndex, int? level)
    {
        if (!PcieLinkPowerSaving.IsValid(level)) level = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).PcieLinkPowerSaving = level);
        if (mode == ActivePowerMode)
            await Task.Run(() => ApplyPcieLinkPowerSavingForMode(mode));
    }

    /// <summary>
    /// Writes the mode's PCIe link power level. The first time, Windows' own values are saved, and they are
    /// written back when PreySense switches to a mode that leaves the setting alone.
    /// </summary>
    internal void ApplyPcieLinkPowerSavingForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            int? level = ProfileForMode(tuning, mode).PcieLinkPowerSaving;
            if (PcieLinkPowerSaving.IsValid(level))
            {
                int? originalAc = tuning.OriginalPcieLinkPowerSavingAc, originalDc = tuning.OriginalPcieLinkPowerSavingDc;
                if (tuning.LastAppliedPcieLinkPowerSaving is null)
                {
                    var read = PcieLinkPowerReader ?? PcieLinkPowerSaving.Read;
                    originalAc = read(false);
                    originalDc = read(true);
                }
                if ((PcieLinkPowerWriter ?? PcieLinkPowerSaving.Apply)(level!.Value))
                {
                    TuningStore.Update(cfg =>
                    {
                        cfg.Tuning.LastAppliedPcieLinkPowerSaving = level;
                        cfg.Tuning.OriginalPcieLinkPowerSavingAc = originalAc;
                        cfg.Tuning.OriginalPcieLinkPowerSavingDc = originalDc;
                    });
                    AppLogger.Log($"[PCIe] {GetModeTitle(mode)}: link power saving {level}.");
                }
                else
                {
                    AppLogger.Error("[PCIe] Windows refused the PCIe link power setting.");
                }
            }
            else if (tuning.LastAppliedPcieLinkPowerSaving is not null)
            {
                if ((PcieLinkPowerRestorer ?? PcieLinkPowerSaving.Restore)(
                        tuning.OriginalPcieLinkPowerSavingAc, tuning.OriginalPcieLinkPowerSavingDc))
                {
                    TuningStore.Update(cfg =>
                    {
                        cfg.Tuning.LastAppliedPcieLinkPowerSaving = null;
                        cfg.Tuning.OriginalPcieLinkPowerSavingAc = null;
                        cfg.Tuning.OriginalPcieLinkPowerSavingDc = null;
                    });
                    AppLogger.Log($"[PCIe] {GetModeTitle(mode)}: Windows' link power setting restored.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PCIe] " + ex.Message);
        }
    }

    internal int? CpuMaxStateForModeIndex(int modeIndex, bool efficientCores) =>
        efficientCores ? SavedTuningMode(modeIndex).CpuMaxStateEfficientPercent : SavedTuningMode(modeIndex).CpuMaxStatePercent;

    /// <summary>Saves a mode's P-core or E-core maximum processor state (null = leave Windows' setting) and applies it if that mode is active.</summary>
    internal async Task SetCpuMaxStateForModeIndexAsync(int modeIndex, bool efficientCores, int? percent)
    {
        if (!CpuMaxProcessorState.IsValid(percent)) percent = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg =>
        {
            var profile = ProfileForMode(cfg.Tuning, mode);
            if (efficientCores) profile.CpuMaxStateEfficientPercent = percent;
            else profile.CpuMaxStatePercent = percent;
        });
        if (mode == ActivePowerMode)
            await Task.Run(() => ApplyCpuMaxStateForMode(mode));
    }

    /// <summary>
    /// Writes the mode's maximum processor state for the P-cores and E-cores. The first time each is changed,
    /// Windows' own values are saved, and they are written back when PreySense switches to a mode that leaves
    /// that setting alone.
    /// </summary>
    internal void ApplyCpuMaxStateForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            var profile = ProfileForMode(tuning, mode);
            ApplyHeldCpuMaxState(mode, CpuMaxProcessorState.PerformanceCores, "P-cores", profile.CpuMaxStatePercent,
                tuning.LastAppliedCpuMaxStatePercent, tuning.OriginalCpuMaxStatePercentAc, tuning.OriginalCpuMaxStatePercentDc,
                (t, last, ac, dc) => { t.LastAppliedCpuMaxStatePercent = last; t.OriginalCpuMaxStatePercentAc = ac; t.OriginalCpuMaxStatePercentDc = dc; });
            ApplyHeldCpuMaxState(mode, CpuMaxProcessorState.EfficientCores, "E-cores", profile.CpuMaxStateEfficientPercent,
                tuning.LastAppliedCpuMaxStateEfficientPercent, tuning.OriginalCpuMaxStateEfficientPercentAc, tuning.OriginalCpuMaxStateEfficientPercentDc,
                (t, last, ac, dc) => { t.LastAppliedCpuMaxStateEfficientPercent = last; t.OriginalCpuMaxStateEfficientPercentAc = ac; t.OriginalCpuMaxStateEfficientPercentDc = dc; });
        }
        catch (Exception ex)
        {
            AppLogger.Error("[CPU state] " + ex.Message);
        }
    }

    private void ApplyHeldCpuMaxState(byte mode, PowerPlanSetting setting, string label, int? wanted,
        int? lastApplied, int? originalAc, int? originalDc, Action<TuningConfig, int?, int?, int?> save)
    {
        if (CpuMaxProcessorState.IsValid(wanted))
        {
            if (lastApplied is null)
            {
                var read = CpuMaxStateReader ?? CpuMaxProcessorState.Read;
                originalAc = read(setting, false);
                originalDc = read(setting, true);
            }
            if ((CpuMaxStateWriter ?? CpuMaxProcessorState.Apply)(setting, wanted!.Value))
            {
                TuningStore.Update(cfg => save(cfg.Tuning, wanted, originalAc, originalDc));
                AppLogger.Log($"[CPU state] {GetModeTitle(mode)}: {label} max processor state {wanted}%.");
            }
            else
            {
                AppLogger.Error($"[CPU state] Windows refused the {label} max processor state.");
            }
        }
        else if (lastApplied is not null)
        {
            if ((CpuMaxStateRestorer ?? CpuMaxProcessorState.Restore)(setting, originalAc, originalDc))
            {
                TuningStore.Update(cfg => save(cfg.Tuning, null, null, null));
                AppLogger.Log($"[CPU state] {GetModeTitle(mode)}: Windows' {label} max processor state restored.");
            }
        }
    }

    internal int? PreferredGpuForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).PreferredGpu;

    /// <summary>Saves a mode's NVIDIA preferred GPU (null = leave NVIDIA's setting) and applies it if that mode is active.</summary>
    internal async Task SetPreferredGpuForModeIndexAsync(int modeIndex, int? value)
    {
        if (!NvidiaPreferredGpu.IsValid(value)) value = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).PreferredGpu = value);
        if (mode == ActivePowerMode)
            await Task.Run(() => ApplyPreferredGpuForMode(mode, force: true));
    }

    /// <summary>
    /// Writes the mode's preferred GPU to NVIDIA's global settings. Nothing is sent when PreySense already set
    /// that value, so switching between modes with the same choice never touches the driver. The first time,
    /// NVIDIA's own values are saved, and they are written back for modes that leave the setting alone. While
    /// the RTX is switched off (iGPU Only) NVIDIA's settings can't be reached; it is sent when the RTX comes back.
    /// </summary>
    internal void ApplyPreferredGpuForMode(byte mode) => ApplyPreferredGpuForMode(mode, force: false);

    internal void ApplyPreferredGpuForMode(byte mode, bool force)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            lock (PreferredGpuGate)
            {
                var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
                int? wanted = ProfileForMode(tuning, mode).PreferredGpu;
                if (NvidiaPreferredGpu.IsValid(wanted))
                {
                    if (!force && tuning.LastAppliedPreferredGpu == wanted) return;
                    NvidiaPreferredGpuValues original = new(
                        tuning.OriginalPreferredGpuMcCompat is long m ? (uint)m : null,
                        tuning.OriginalPreferredGpuRenderingMode is long r ? (uint)r : null);
                    if (tuning.LastAppliedPreferredGpu is null)
                    {
                        var (readResult, values) = PreferredGpuReader?.Invoke() ?? ReadPreferredGpu();
                        if (readResult != NvidiaSettingsResult.Ok) { LogPreferredGpuResult(mode, readResult); return; }
                        original = values;
                    }
                    var result = (PreferredGpuWriter ?? NvidiaPreferredGpu.Apply)((uint)wanted!.Value);
                    if (result == NvidiaSettingsResult.Ok)
                    {
                        TuningStore.Update(cfg =>
                        {
                            cfg.Tuning.LastAppliedPreferredGpu = wanted;
                            cfg.Tuning.OriginalPreferredGpuMcCompat = original.McCompat;
                            cfg.Tuning.OriginalPreferredGpuRenderingMode = original.RenderingMode;
                        });
                        AppLogger.Log($"[Preferred GPU] {GetModeTitle(mode)}: {PreferredGpuName(wanted.Value)}.");
                    }
                    else LogPreferredGpuResult(mode, result);
                }
                else if (tuning.LastAppliedPreferredGpu is not null)
                {
                    var original = new NvidiaPreferredGpuValues(
                        tuning.OriginalPreferredGpuMcCompat is long m ? (uint)m : null,
                        tuning.OriginalPreferredGpuRenderingMode is long r ? (uint)r : null);
                    var result = (PreferredGpuRestorer ?? NvidiaPreferredGpu.Restore)(original);
                    if (result == NvidiaSettingsResult.Ok)
                    {
                        TuningStore.Update(cfg =>
                        {
                            cfg.Tuning.LastAppliedPreferredGpu = null;
                            cfg.Tuning.OriginalPreferredGpuMcCompat = null;
                            cfg.Tuning.OriginalPreferredGpuRenderingMode = null;
                        });
                        AppLogger.Log($"[Preferred GPU] {GetModeTitle(mode)}: NVIDIA's own setting restored.");
                    }
                    else LogPreferredGpuResult(mode, result);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Preferred GPU] " + ex.Message);
        }
    }

    private static (NvidiaSettingsResult, NvidiaPreferredGpuValues) ReadPreferredGpu()
    {
        var result = NvidiaPreferredGpu.Read(out var values);
        return (result, values);
    }

    private static string PreferredGpuName(int value) => (uint)value switch
    {
        NvidiaPreferredGpu.Integrated => "Integrated graphics",
        NvidiaPreferredGpu.Nvidia => "NVIDIA",
        _ => "Auto-select"
    };

    private void LogPreferredGpuResult(byte mode, NvidiaSettingsResult result)
    {
        if (result == NvidiaSettingsResult.NoNvidiaGpu)
            AppLogger.Log($"[Preferred GPU] {GetModeTitle(mode)}: RTX is off; will apply when it is switched back on.");
        else
            AppLogger.Error($"[Preferred GPU] {GetModeTitle(mode)}: NVIDIA's settings could not be changed.");
    }
}
