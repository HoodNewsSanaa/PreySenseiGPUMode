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
}
