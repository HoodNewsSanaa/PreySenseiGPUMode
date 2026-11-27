using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PreySense.Core.Configuration;
using PreySense.Gpu;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: per-mode "RTX guard". While a guarded mode is active, NVIDIA's polling services are stopped,
/// and whenever the RTX wakes, the apps found on it are set to "Power saving" in Windows' graphics settings so they
/// don't use it again. Services come back and every check stops when a mode without the guard is selected or
/// PreySense quits. Moved apps stay moved until undone from Settings.
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan RtxGuardInterval = TimeSpan.FromSeconds(2);
    private static readonly object _rtxGuardGate = new();
    private Timer? _rtxGuardTimer;
    private bool _rtxGuardActive;
    private HashSet<string>? _rtxLuids;
    private int _rtxGuardChecking;

    /// <summary>Test seams.</summary>
    internal Func<IEnumerable<string>, List<string>>? RtxGuardServiceStopper { get; set; }
    internal Action<IEnumerable<string>>? RtxGuardServiceStarter { get; set; }

    internal bool IsRtxGuardActive => _rtxGuardActive;

    internal bool RtxGuardForModeIndex(int modeIndex) => SavedTuningMode(modeIndex).RtxGuard == true;

    internal void SetRtxGuardForModeIndex(int modeIndex, bool enabled)
    {
        byte mode = ModeIndexToByte(modeIndex);
        TuningStore.Update(cfg => ProfileForMode(cfg.Tuning, mode).RtxGuard = enabled ? true : null);
        if (mode == ActivePowerMode)
            System.Threading.Tasks.Task.Run(() => ApplyRtxGuardForMode(mode));
    }

    /// <summary>Turns the guard on or off to match the mode.</summary>
    internal void ApplyRtxGuardForMode(byte mode)
    {
        try
        {
            if (!ProfileManager.IsSupportedMode(mode)) return;
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            bool wanted = ProfileForMode(tuning, mode).RtxGuard == true;
            lock (_rtxGuardGate)
            {
                if (wanted && !_rtxGuardActive) StartRtxGuardLocked(mode);
                else if (!wanted && (_rtxGuardActive || tuning.RtxGuardStoppedServices is { Count: > 0 })) StopRtxGuardLocked(mode);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX guard] " + ex.Message);
        }
    }

    private void StartRtxGuardLocked(byte mode)
    {
        _rtxGuardActive = true;
        var stopped = (RtxGuardServiceStopper ?? RtxGuard.StopServices)(RtxGuard.PollingServices);
        TuningStore.Update(cfg =>
        {
            var list = cfg.Tuning.RtxGuardStoppedServices ??= new();
            foreach (string name in stopped)
                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        });
        if (RtxGuardServiceStopper is null)
        {
            _rtxGuardTimer ??= new Timer(_ => RtxGuardTick(), null, Timeout.Infinite, Timeout.Infinite);
            _rtxGuardTimer.Change(RtxGuardInterval, RtxGuardInterval);
        }
        AppLogger.Log($"[RTX guard] {GetModeTitle(mode)}: guard on.");
    }

    private void StopRtxGuardLocked(byte? mode)
    {
        _rtxGuardActive = false;
        _rtxGuardTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        var services = tuning.RtxGuardStoppedServices?.ToList() ?? new List<string>();
        if (services.Count > 0)
        {
            (RtxGuardServiceStarter ?? RtxGuard.StartServices)(services);
            TuningStore.Update(cfg => cfg.Tuning.RtxGuardStoppedServices = new());
        }
        AppLogger.Log(mode is byte m ? $"[RTX guard] {GetModeTitle(m)}: guard off." : "[RTX guard] Guard off.");
    }

    /// <summary>At quit or Windows shutdown: give NVIDIA's services back.</summary>
    private void StopRtxGuardForExit()
    {
        try
        {
            lock (_rtxGuardGate)
                if (_rtxGuardActive) StopRtxGuardLocked(null);
        }
        catch { }
    }

    /// <summary>Every 2 s while guarded: only reads Windows' cached RTX state; looks further only when it is awake.</summary>
    private void RtxGuardTick()
    {
        if (Interlocked.Exchange(ref _rtxGuardChecking, 1) != 0) return;
        try
        {
            if (!_rtxGuardActive || _disposed) return;
            PreySense.Helpers.DeviceHelper.InvalidateDisplayDevicePowerStateCache();
            if (PreySense.Helpers.DeviceHelper.GetDisplayDevicePowerState("VEN_10DE") != 1) return; // asleep or off

            // Awake: services that restarted themselves go again.
            if (RtxGuard.AnyRunning(RtxGuard.PollingServices))
            {
                var stopped = RtxGuard.StopServices(RtxGuard.PollingServices);
                if (stopped.Count > 0)
                    TuningStore.Update(cfg =>
                    {
                        var list = cfg.Tuning.RtxGuardStoppedServices ??= new();
                        foreach (string name in stopped)
                            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
                    });
            }

            _rtxLuids ??= RtxGuard.NvidiaAdapterLuids();
            if (_rtxLuids.Count == 0) return;
            var pids = RtxGuard.ProcessesOnAdapters(_rtxLuids);
            if (pids is null || pids.Count == 0) return;
            MoveAppsToIntegrated(pids);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX guard] " + ex.Message);
        }
        finally { Interlocked.Exchange(ref _rtxGuardChecking, 0); }
    }

    private void MoveAppsToIntegrated(IEnumerable<int> pids)
    {
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var moved = new List<string>();
        foreach (int pid in pids)
        {
            string? path = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.Id == Environment.ProcessId || process.SessionId == 0) continue;
                path = process.MainModule?.FileName;
            }
            catch { }
            if (!IsMovableApp(path, windowsDir)) continue;
            if (RtxGuard.SetPowerSaving(path!))
            {
                moved.Add(path!);
                AppLogger.Log($"[RTX guard] {Path.GetFileName(path)} was on the RTX; set to Power saving.");
            }
        }
        if (moved.Count == 0) return;
        TuningStore.Update(cfg =>
        {
            var list = cfg.Tuning.RtxGuardMovedApps ??= new();
            foreach (string path in moved)
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        });
        string names = string.Join(", ", moved.Select(Path.GetFileNameWithoutExtension));
        var message = PreySense.Frontend.Services.LocalizationService.Format(
            "RTX guard: {0} woke the RTX and now uses the integrated GPU. Restart it to finish moving it.", names);
        if (_dispatcher is null) DiagnosticMessage = message;
        else _dispatcher.BeginInvoke(() => DiagnosticMessage = message);
    }

    /// <summary>Apps that can be moved: user programs only (not Windows' own, NVIDIA's or PreySense).</summary>
    internal static bool IsMovableApp(string? path, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        if (path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("\\NVIDIA Corporation\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(Path.GetFileName(path), "PreySense.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<string> RtxGuardMovedApps =>
        (TuningStore.GetCurrent().Tuning.RtxGuardMovedApps ?? new()).ToList();

    /// <summary>Gives every app the guard moved its old graphics choice back.</summary>
    internal int UndoRtxGuardMovedApps()
    {
        var apps = RtxGuardMovedApps;
        foreach (string path in apps) RtxGuard.RemovePowerSaving(path);
        TuningStore.Update(cfg => cfg.Tuning.RtxGuardMovedApps = new());
        return apps.Count;
    }
}
