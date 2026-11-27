using System;
using PreySense.Core.Driver;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    // Per-mode CPU boost and Tau helpers live separately from driver tuning
    // and calibration workflows so mode editing is easier to navigate.
    public static readonly string[] CpuBoostModeNames =
    [
        "Disabled",
        "Enabled",
        "Aggressive",
        "Efficient Enabled",
        "Efficient Aggressive",
        "Aggressive At Guaranteed",
        "Efficient Aggressive At Guaranteed"
    ];

    public int CpuBoostForMode(byte mode) => mode switch
    {
        0x05 => CustomTurboCpuBoost,
        0x04 => CustomPerfCpuBoost,
        0x01 => CustomBalancedCpuBoost,
        0x06 => CustomEcoCpuBoost,
        _ => CustomQuietCpuBoost
    };

    public int CpuBoostForModeIndex(int modeIndex) => modeIndex switch
    {
        0 => CustomEcoCpuBoost,
        1 => CustomQuietCpuBoost,
        2 => CustomBalancedCpuBoost,
        3 => CustomPerfCpuBoost,
        _ => CustomTurboCpuBoost
    };

    public void SetCpuBoostForModeIndex(int modeIndex, int boostMode)
    {
        int boost = Math.Clamp(boostMode, 0, 6);
        switch (modeIndex)
        {
            case 0: CustomEcoCpuBoost = boost; break;
            case 1: CustomQuietCpuBoost = boost; break;
            case 2: CustomBalancedCpuBoost = boost; break;
            case 3: CustomPerfCpuBoost = boost; break;
            default: CustomTurboCpuBoost = boost; break;
        }
    }

    public int CpuBoostModeForMode(byte mode) => Math.Clamp(CpuBoostForMode(mode), 0, 6);

    public int CpuBoostModeForModeIndex(int index) => Math.Clamp(CpuBoostForModeIndex(index), 0, 6);

    private bool _lastCpuBoostApplySucceeded = true;

    internal Func<int, bool>? CpuBoostWriter { get; set; }

    private bool IsModeCpuBoostTuningEnabled(byte mode)
    {
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        return tuning.EnableCpuBoostTuningControl && ProfileForMode(tuning, mode).ApplyCpuBoost;
    }

    public bool ApplyCpuBoostForMode(byte mode)
    {
        QueueModeExtras(mode);
        if (!IsModeCpuBoostTuningEnabled(mode)) return true;
        int boost = CpuBoostModeForMode(mode);
        if (_dispatcher == null || _dispatcher.CheckAccess()) CpuBoostMode = boost;
        else _ = _dispatcher.BeginInvoke(() => CpuBoostMode = boost);
        _lastCpuBoostApplySucceeded = (CpuBoostWriter ?? PowerLimitController.SetCpuBoost)(boost);
        if (!_lastCpuBoostApplySucceeded) DiagnosticMessage = "Could not apply CPU boost.";
        return _lastCpuBoostApplySucceeded;
    }

    private static readonly object ModeExtrasLock = new();
    private System.Threading.Tasks.Task? _modeExtrasChain;
    private int _modeExtrasGeneration;

    /// <summary>
    /// iGPU Only fork: the Mode Extras (CPU caps, EPP, PCIe, NVIDIA settings, keyboard lighting, RTX guard) run in
    /// the background, one switch at a time, after the mode itself is confirmed. Some of them can take seconds
    /// (stopping NVIDIA's services, waking NVIDIA's driver), and the mode switcher must not wait for them. When
    /// modes are switched quickly, only the latest mode's extras are applied.
    /// </summary>
    private void QueueModeExtras(byte mode)
    {
        if (_dispatcher is null) { ApplyModeExtrasNow(mode); return; } // tests and design-time: run in place
        int generation = System.Threading.Interlocked.Increment(ref _modeExtrasGeneration);
        lock (ModeExtrasLock)
        {
            var previous = _modeExtrasChain ?? System.Threading.Tasks.Task.CompletedTask;
            _modeExtrasChain = previous.ContinueWith(_ =>
            {
                if (generation != System.Threading.Volatile.Read(ref _modeExtrasGeneration) || _disposed) return;
                ApplyModeExtrasNow(mode);
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void ApplyModeExtrasNow(byte mode)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        ApplyCpuFrequencyCapForMode(mode);
        ApplyCpuMaxStateForMode(mode);
        ApplyProcessorPolicyForMode(mode);
        ApplyPcieLinkPowerSavingForMode(mode);
        ApplyKeyboardLightingForMode(mode);
        ApplyPhysxForMode(mode);
        // NVIDIA's driver and services: never at the same time as a GPU mode switch or an RTX restart
        // (overlapping them froze the system).
        if (_dispatcher is null) // tests
        {
            ApplyGpuClockLockForMode(mode);
            ApplyPreferredGpuForMode(mode);
            ApplyRtxGuardForMode(mode);
        }
        else
        {
            for (int waited = 0; System.Threading.Volatile.Read(ref _gpuSwitchInProgress) != 0 && waited < 120; waited++)
                System.Threading.Thread.Sleep(500);
            using var nvidia = PreySense.Core.Services.HardwareOperationGate.TryEnterNvidiaWork(TimeSpan.FromSeconds(60));
            if (nvidia is null || System.Threading.Volatile.Read(ref _gpuSwitchInProgress) != 0)
            {
                AppLogger.Error($"[Mode extras] {GetModeTitle(mode)}: GPU busy; NVIDIA settings will apply on the next mode switch.");
            }
            else
            {
                ApplyGpuClockLockForMode(mode);
                ApplyPreferredGpuForMode(mode);
                ApplyRtxGuardForMode(mode);
            }
        }
        if (started.ElapsedMilliseconds > 3000)
            AppLogger.Log($"[Mode extras] {GetModeTitle(mode)} took {started.ElapsedMilliseconds} ms (in the background).");
    }

    private async System.Threading.Tasks.Task<bool> ApplyCpuBoostForModeAsync(byte mode)
    {
        QueueModeExtras(mode);
        if (!IsModeCpuBoostTuningEnabled(mode)) return true;
        return await WriteCpuBoostAsync(mode);
    }

    private async System.Threading.Tasks.Task<bool> WriteCpuBoostAsync(byte mode)
    {
        int boost = CpuBoostModeForMode(mode);
        if (_dispatcher == null || _dispatcher.CheckAccess()) CpuBoostMode = boost;
        else _ = _dispatcher.BeginInvoke(() => CpuBoostMode = boost);
        bool applied = await System.Threading.Tasks.Task.Run(() => (CpuBoostWriter ?? PowerLimitController.SetCpuBoost)(boost));
        _lastCpuBoostApplySucceeded = applied;
        if (!applied) DiagnosticMessage = "Could not apply CPU boost.";
        return applied;
    }

    public int TauForMode(byte mode) => Math.Clamp(mode switch
    {
        0x05 => CustomTurboTau,
        0x04 => CustomPerfTau,
        0x01 => CustomBalancedTau,
        0x06 => CustomEcoTau,
        _ => CustomQuietTau
    }, 0, CpuPowerLimitConfiguration.InfiniteTauSliderValue);

    public int TauForModeIndex(int modeIndex) => modeIndex switch
    {
        0 => CustomEcoTau,
        1 => CustomQuietTau,
        2 => CustomBalancedTau,
        3 => CustomPerfTau,
        _ => CustomTurboTau
    };

    public void SetTauForModeIndex(int modeIndex, int tauSeconds)
    {
        int tau = Math.Clamp(tauSeconds, 0, CpuPowerLimitConfiguration.InfiniteTauSliderValue);
        switch (modeIndex)
        {
            case 0: CustomEcoTau = tau; break;
            case 1: CustomQuietTau = tau; break;
            case 2: CustomBalancedTau = tau; break;
            case 3: CustomPerfTau = tau; break;
            default: CustomTurboTau = tau; break;
        }
        TauSeconds = tau;
    }

    public int EffectivePlatformPl1Limit => CpuPowerLimitConfiguration.MaxPowerLimitWatts;

    public void ClampCpuPl1ToPlatformLimit()
    {
        Pl1Max = EffectivePlatformPl1Limit;
        CustomQuietPl1 = LimitPl1ToPlatform(CustomQuietPl1);
        CustomEcoPl1 = LimitPl1ToPlatform(CustomEcoPl1);
        CustomBalancedPl1 = LimitPl1ToPlatform(CustomBalancedPl1);
        CustomPerfPl1 = LimitPl1ToPlatform(CustomPerfPl1);
        CustomTurboPl1 = LimitPl1ToPlatform(CustomTurboPl1);
        Pl1Watts = LimitPl1ToPlatform(Pl1Watts);
        CustomQuietPl2 = LimitPl2(CustomQuietPl1, CustomQuietPl2);
        CustomEcoPl2 = LimitPl2(CustomEcoPl1, CustomEcoPl2);
        CustomBalancedPl2 = LimitPl2(CustomBalancedPl1, CustomBalancedPl2);
        CustomPerfPl2 = LimitPl2(CustomPerfPl1, CustomPerfPl2);
        CustomTurboPl2 = LimitPl2(CustomTurboPl1, CustomTurboPl2);
        Pl2Watts = LimitPl2(Pl1Watts, Pl2Watts);
    }

    public int LimitPl1ToPlatform(int pl1Watts) =>
        Math.Clamp(pl1Watts, 5, EffectivePlatformPl1Limit);

    public int LimitPl2(int pl1Watts, int pl2Watts) =>
        Math.Clamp(pl2Watts, LimitPl1ToPlatform(pl1Watts),
            CpuPowerLimitConfiguration.MaxPowerLimitWatts);
}
