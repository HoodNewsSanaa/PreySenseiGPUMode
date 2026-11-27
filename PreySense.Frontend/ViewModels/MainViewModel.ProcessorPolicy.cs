using System;
using System.Threading.Tasks;
using PreySense.Core.Configuration;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: per-mode energy performance preference (EPP), core scheduling (prefer / only E-cores) and
/// keyboard lighting off. The Windows settings are saved the first time PreySense changes them and written back
/// for modes that leave them alone.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Test seams for processor power settings: write a value, restore AC/DC, read AC (false) or DC (true).</summary>
    internal Func<PowerPlanSetting, int, bool>? PlanSettingWriter { get; set; }
    internal Func<PowerPlanSetting, int?, int?, bool>? PlanSettingRestorer { get; set; }
    internal Func<PowerPlanSetting, bool, int?>? PlanSettingReader { get; set; }

    /// <summary>True while the active mode has the keyboard lighting switched off.</summary>
    private volatile bool _modeLightingOff;

    internal bool IsModeLightingOff => _modeLightingOff;

    internal int? EppForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).EppPercent;
    internal int? CoreSchedulingForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).CoreSchedulingPolicy;
    internal bool KeyboardLightingOffForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).KeyboardLightingOff == true;

    internal async Task SetEppForModeIndexAsync(int modeIndex, int? percent)
    {
        if (!ProcessorPolicySettings.IsValidEpp(percent)) percent = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).EppPercent = percent);
        if (mode == ActivePowerMode) await Task.Run(() => ApplyProcessorPolicyForMode(mode));
    }

    internal async Task SetCoreSchedulingForModeIndexAsync(int modeIndex, int? policy)
    {
        if (!ProcessorPolicySettings.IsValidScheduling(policy)) policy = null;
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).CoreSchedulingPolicy = policy);
        if (mode == ActivePowerMode) await Task.Run(() => ApplyProcessorPolicyForMode(mode));
    }

    internal void SetKeyboardLightingOffForModeIndex(int modeIndex, bool off)
    {
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).KeyboardLightingOff = off ? true : null);
        if (mode == ActivePowerMode) ApplyKeyboardLightingForMode(mode);
    }

    /// <summary>Writes the mode's EPP (both core types) and core scheduling (long and short threads).</summary>
    internal void ApplyProcessorPolicyForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            var profile = ProfileForMode(tuning, mode);
            int? epp = ProcessorPolicySettings.IsValidEpp(profile.EppPercent) ? profile.EppPercent : null;
            int? scheduling = ProcessorPolicySettings.IsValidScheduling(profile.CoreSchedulingPolicy) ? profile.CoreSchedulingPolicy : null;
            ApplyHeldPlanSetting(mode, ProcessorPolicySettings.EppPerformanceCores, epp);
            ApplyHeldPlanSetting(mode, ProcessorPolicySettings.EppEfficientCores, epp);
            ApplyHeldPlanSetting(mode, ProcessorPolicySettings.LongThreadScheduling, scheduling);
            ApplyHeldPlanSetting(mode, ProcessorPolicySettings.ShortThreadScheduling, scheduling);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[CPU policy] " + ex.Message);
        }
    }

    private void ApplyHeldPlanSetting(byte mode, PowerPlanSetting setting, int? wanted)
    {
        string key = setting.Setting.ToString("D");
        var current = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        HeldPlanSettingState? held = null;
        current.HeldPlanSettings?.TryGetValue(key, out held);

        if (wanted is int value)
        {
            if (held?.LastApplied == value && (PowerPlanGuard.HeldValue(setting) == (uint)value || PlanSettingWriter != null))
                return; // already in place and still held
            int? originalAc = held?.OriginalAc, originalDc = held?.OriginalDc;
            if (held is null)
            {
                var read = PlanSettingReader ?? ProcessorPolicySettings.Read;
                originalAc = read(setting, false);
                originalDc = read(setting, true);
            }
            if ((PlanSettingWriter ?? ProcessorPolicySettings.Apply)(setting, value))
            {
                TuningStore.Update(cfg =>
                {
                    cfg.Tuning.HeldPlanSettings ??= new();
                    cfg.Tuning.HeldPlanSettings[key] = new HeldPlanSettingState
                    {
                        LastApplied = value, OriginalAc = originalAc, OriginalDc = originalDc
                    };
                });
                AppLogger.Log($"[CPU policy] {GetModeTitle(mode)}: {setting.Name} = {value}.");
            }
            else AppLogger.Error($"[CPU policy] Windows refused {setting.Name}.");
        }
        else if (held is not null)
        {
            if ((PlanSettingRestorer ?? ProcessorPolicySettings.Restore)(setting, held.OriginalAc, held.OriginalDc))
            {
                TuningStore.Update(cfg => cfg.Tuning.HeldPlanSettings?.Remove(key));
                AppLogger.Log($"[CPU policy] {GetModeTitle(mode)}: Windows' {setting.Name} restored.");
            }
        }
    }

    /// <summary>Turns the keyboard and lid lighting off or back on to match the mode. The saved lighting never changes.</summary>
    internal void ApplyKeyboardLightingForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            bool off = ProfileForMode(tuning, mode).KeyboardLightingOff == true;
            if (off == _modeLightingOff) return;
            _modeLightingOff = off;
            AppLogger.Log($"[Lighting] {GetModeTitle(mode)}: keyboard lighting {(off ? "off" : "back on")}.");
            if (_dispatcher is null || _disposed) return;
            ApplySavedLighting();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Lighting] " + ex.Message);
        }
    }
}
