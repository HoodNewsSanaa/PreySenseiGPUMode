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
}
