using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PreySense.Core.Configuration;
using PreySense.Gpu;
using PreySense.Mode;

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
    private bool _rtxGuardSawAsleep = true;
    private DateTime _rtxGuardLastLook = DateTime.MinValue;

    /// <summary>
    /// Reading the GPU counters touches the RTX, and NVIDIA's driver only powers it down after about 5 seconds
    /// without any activity. So the guard looks once just after each wake, then at most every 30 seconds while
    /// the RTX stays awake; checking more often would itself keep it awake.
    /// </summary>
    private static readonly TimeSpan RtxGuardLookInterval = TimeSpan.FromSeconds(30);

    /// <summary>Test seams.</summary>
    internal Func<IEnumerable<string>, List<string>>? RtxGuardServiceStopper { get; set; }
    internal Action<IEnumerable<string>>? RtxGuardServiceStarter { get; set; }

    internal bool IsRtxGuardActive => _rtxGuardActive;

    /// <summary>
    /// True while the active mode's preferred GPU is Integrated graphics: apps found on the RTX are moved off it
    /// automatically (set to Power saving, and the RTX restarted when only everyday apps hold it).
    /// </summary>
    private volatile bool _autoMoveActive;
    private DateTime _lastAutoRestart = DateTime.MinValue;
    private static readonly TimeSpan AutoRestartMinInterval = TimeSpan.FromSeconds(60);

    internal bool IsAutoMoveActive => _autoMoveActive;

    /// <summary>The watcher (timer) runs for the RTX guard and for automatic moving.</summary>
    private bool WatcherWanted => _rtxGuardActive || _autoMoveActive;

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
            // Earlier builds also stopped NVIDIA's Display Container; give it back right away if they did.
            var noLongerStopped = tuning.RtxGuardStoppedServices?
                .Where(name => !RtxGuard.PollingServices.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (noLongerStopped is { Count: > 0 })
            {
                (RtxGuardServiceStarter ?? RtxGuard.StartServices)(noLongerStopped);
                TuningStore.Update(cfg => cfg.Tuning.RtxGuardStoppedServices?.RemoveAll(name =>
                    noLongerStopped.Contains(name, StringComparer.OrdinalIgnoreCase)));
            }
            bool autoMove = ProfileForMode(tuning, mode).PreferredGpu == (int)NvidiaPreferredGpu.Integrated;
            lock (_rtxGuardGate)
            {
                if (wanted && !_rtxGuardActive) StartRtxGuardLocked(mode);
                else if (!wanted && (_rtxGuardActive || tuning.RtxGuardStoppedServices is { Count: > 0 })) StopRtxGuardLocked(mode);
                if (autoMove != _autoMoveActive)
                {
                    _autoMoveActive = autoMove;
                    _rtxGuardSawAsleep = true; // look right away if the RTX is awake now
                    AppLogger.Log($"[RTX guard] {GetModeTitle(mode)}: automatic moving {(autoMove ? "on" : "off")}.");
                }
                UpdateWatcherTimerLocked();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX guard] " + ex.Message);
        }
    }

    /// <summary>The guard is on but hasn't stopped NVIDIA's services yet (the screen was on the RTX).</summary>
    private volatile bool _rtxGuardServicesPending;

    private void StartRtxGuardLocked(byte mode)
    {
        _rtxGuardActive = true;
        _rtxGuardServicesPending = true;
        TryStopGuardServices();
        AppLogger.Log($"[RTX guard] {GetModeTitle(mode)}: guard on.");
    }

    /// <summary>
    /// Stops NVIDIA's polling services, but only while the laptop screen is on Intel: the Display Container must
    /// keep running while the RTX drives the screen.
    /// </summary>
    private void TryStopGuardServices()
    {
        if (RtxGuardServiceStopper is null)
        {
            bool? screenOnRtx;
            try { screenOnRtx = PreySense.Helpers.DeviceHelper.IsDesktopScreenOnAdapter("NVIDIA"); }
            catch { screenOnRtx = null; }
            if (screenOnRtx != false || Volatile.Read(ref _gpuSwitchInProgress) != 0) return; // try again later
        }
        var stopped = (RtxGuardServiceStopper ?? RtxGuard.StopServices)(RtxGuard.PollingServices);
        _rtxGuardServicesPending = false;
        if (stopped.Count == 0) return;
        TuningStore.Update(cfg =>
        {
            var list = cfg.Tuning.RtxGuardStoppedServices ??= new();
            foreach (string name in stopped)
                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        });
    }

    private void UpdateWatcherTimerLocked()
    {
        if (RtxGuardServiceStopper is not null) return; // tests
        if (WatcherWanted)
        {
            _rtxGuardTimer ??= new Timer(_ => RtxGuardTick(), null, Timeout.Infinite, Timeout.Infinite);
            _rtxGuardTimer.Change(RtxGuardInterval, RtxGuardInterval);
        }
        else _rtxGuardTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StopRtxGuardLocked(byte? mode)
    {
        _rtxGuardActive = false;
        _rtxGuardServicesPending = false;
        UpdateWatcherTimerLocked();
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        var services = tuning.RtxGuardStoppedServices?.ToList() ?? new List<string>();
        if (services.Count > 0)
        {
            (RtxGuardServiceStarter ?? RtxGuard.StartServices)(services);
            TuningStore.Update(cfg => cfg.Tuning.RtxGuardStoppedServices = new());
        }
        AppLogger.Log(mode is byte m ? $"[RTX guard] {GetModeTitle(m)}: guard off." : "[RTX guard] Guard off.");
    }

    /// <summary>
    /// NVIDIA's display switching needs the Display Container service. Before a GPU mode switch, starts it again if
    /// the guard stopped it; returns true when the guard should stop it again afterwards.
    /// </summary>
    internal bool ResumeNvidiaDisplayServiceForSwitch()
    {
        if (!_rtxGuardActive) return false;
        if (!RtxGuard.StartAndWait(RtxGuard.DisplayContainerService, TimeSpan.FromSeconds(15))) return false;
        Thread.Sleep(1500); // let NVIDIA's API come up
        return true;
    }

    /// <summary>After a GPU mode switch: the guard stops the Display Container again if it is still on.</summary>
    internal void PauseNvidiaDisplayServiceAfterSwitch(bool resumed)
    {
        if (!resumed || !_rtxGuardActive) return;
        // After the switch settles; TryStopGuardServices also skips this if the screen is now on the RTX.
        Thread.Sleep(3000);
        try
        {
            using var lease = PreySense.Core.Services.HardwareOperationGate.TryEnterNvidiaWork(TimeSpan.FromSeconds(30));
            if (lease is null) { _rtxGuardServicesPending = true; return; }
            lock (_rtxGuardGate)
                if (_rtxGuardActive) TryStopGuardServices();
        }
        catch (Exception ex) { AppLogger.Error("[RTX guard] " + ex.Message); }
    }

    /// <summary>At quit or Windows shutdown: give NVIDIA's services back.</summary>
    private void StopRtxGuardForExit()
    {
        // Runs on the UI thread at quit: never wait long for a guard change still in progress.
        bool locked = false;
        try
        {
            Monitor.TryEnter(_rtxGuardGate, TimeSpan.FromSeconds(2), ref locked);
            if (_rtxGuardActive || _autoMoveActive || !locked)
            {
                _rtxGuardActive = false;
                _autoMoveActive = false;
                _rtxGuardTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                var services = TuningStore.GetCurrent().Tuning.RtxGuardStoppedServices?.ToList();
                if (services is { Count: > 0 })
                {
                    RtxGuard.StartServices(services); // Start() only sends the request: quick
                    TuningStore.Update(cfg => cfg.Tuning.RtxGuardStoppedServices = new());
                }
            }
        }
        catch { }
        finally { if (locked) Monitor.Exit(_rtxGuardGate); }
    }

    /// <summary>Every 2 s while guarded: only reads Windows' cached RTX state; looks further only when it is awake.</summary>
    private void RtxGuardTick()
    {
        if (Interlocked.Exchange(ref _rtxGuardChecking, 1) != 0) return;
        try
        {
            if (!WatcherWanted || _disposed) return;
            if (Volatile.Read(ref _gpuSwitchInProgress) != 0) return; // leave the GPU alone while it switches
            if (_rtxGuardActive && _rtxGuardServicesPending)
                lock (_rtxGuardGate)
                    if (_rtxGuardActive && _rtxGuardServicesPending) TryStopGuardServices();
            PreySense.Helpers.DeviceHelper.InvalidateDisplayDevicePowerStateCache();
            if (PreySense.Helpers.DeviceHelper.GetDisplayDevicePowerState("VEN_10DE") != 1)
            {
                _rtxGuardSawAsleep = true; // asleep or off: nothing to do
                return;
            }
            DateTime now = DateTime.UtcNow;
            bool justWoke = _rtxGuardSawAsleep;
            _rtxGuardSawAsleep = false;
            if (!justWoke && now - _rtxGuardLastLook < RtxGuardLookInterval) return;
            if (justWoke)
            {
                // Let the app that woke it get going, so it shows up in the counters.
                Thread.Sleep(1000);
            }
            _rtxGuardLastLook = DateTime.UtcNow;

            // Awake: services that restarted themselves go again (never during a GPU switch).
            if (_rtxGuardActive && !_rtxGuardServicesPending && RtxGuard.AnyRunning(RtxGuard.PollingServices))
                lock (_rtxGuardGate)
                    if (_rtxGuardActive) TryStopGuardServices();

            // Re-read every time: the RTX gets a new adapter ID whenever it is restarted (iGPU Only, Move apps).
            _rtxLuids = RtxGuard.NvidiaAdapterLuids();
            if (_rtxLuids.Count == 0) return;
            var pids = RtxGuard.ProcessesOnAdapters(_rtxLuids);
            if (pids is null || pids.Count == 0) return;
            MoveAppsToIntegrated(pids);
            if (_autoMoveActive) MaybeRestartRtxForMove(pids);
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

    /// <summary>
    /// Automatic moving (modes whose preferred GPU is Integrated graphics): whenever anything holds the RTX
    /// (apps, games, or Windows' own desktop compositor), the RTX is restarted so everything lets go of it and it
    /// can sleep. Apps were set to Power saving first, so they come back on Intel. Never while the RTX drives the
    /// screen, and at most once a minute so an app that keeps grabbing it can't cause a restart loop.
    /// </summary>
    private void MaybeRestartRtxForMove(IEnumerable<int> pids)
    {
        if (DateTime.UtcNow - _lastAutoRestart < AutoRestartMinInterval) return;
        var holders = new List<string>();
        foreach (int pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); holders.Add(p.ProcessName); }
            catch { } // already gone
        }
        if (holders.Count == 0 || _dispatcher is null) return;
        _lastAutoRestart = DateTime.UtcNow;
        string names = string.Join(", ", holders.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => IsWindowsGpuHolder(name) ? "Windows (" + name + ")" : name));
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (!_autoMoveActive || IsRtxAsleepOrOff()) return;
                if (await IsDiscreteGpuDrivingScreenAsync()) return;
                AppLogger.Log("[RTX guard] Kicking everything off the RTX: " + names);
                string result = await MoveAppsOffDiscreteGpuAsync();
                DiagnosticMessage = PreySense.Frontend.Services.LocalizationService.Format(
                    "Moved off the RTX: {0}.", names) + " " + PreySense.Frontend.Services.LocalizationService.Translate(result);
            }
            catch (Exception ex) { AppLogger.Error("[RTX guard] " + ex.Message); }
        });
    }

    /// <summary>Windows' own processes that keep memory on a GPU (the desktop compositor and friends).</summary>
    internal static bool IsWindowsGpuHolder(string processName) =>
        processName.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("csrss", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("System", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase);

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
