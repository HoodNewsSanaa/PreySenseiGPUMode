using System;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: "Sleep in Background". While the main window is hidden or minimized, PreySense runs in
/// Windows' efficiency mode (EcoQoS: low clocks, efficient cores, no raised timer resolution), releases NVML,
/// and checks the iGPU Only state every 2 minutes instead of every 20 seconds. Sensor polling already stops
/// while the window is hidden; with this setting it also stops while the window is minimized.
/// Everything comes back as soon as the window is shown; the first readings take about a second.
/// </summary>
public partial class MainViewModel
{
    private const int IgpuOnlyRecheckAsleepSeconds = 120;
    private bool _backgroundAsleep;

    /// <summary>Test seam: applies or removes efficiency mode for this process.</summary>
    internal Action<bool>? EfficiencyModeSetter { get; set; }

    public bool IsSleepInBackgroundEnabled
    {
        get
        {
            try { return TuningStore.GetCurrent().General.SleepInBackground; }
            catch { return true; }
        }
    }

    internal bool IsBackgroundAsleep => _backgroundAsleep;

    private TimeSpan IgpuOnlyRecheckInterval =>
        TimeSpan.FromSeconds(_backgroundAsleep ? IgpuOnlyRecheckAsleepSeconds : IgpuOnlyRecheckSeconds);

    [RelayCommand]
    private void ToggleSleepInBackground()
    {
        bool enabled = !IsSleepInBackgroundEnabled;
        try { TuningStore.Update(cfg => cfg.General.SleepInBackground = enabled); }
        catch (Exception ex)
        {
            DiagnosticMessage = $"Could not save Sleep in Background: {ex.Message}";
            return;
        }
        OnPropertyChanged(nameof(IsSleepInBackgroundEnabled));
        if (!enabled) SetBackgroundSleep(false);
        DiagnosticMessage = enabled
            ? "PreySense will sleep while its window is hidden or minimized."
            : "PreySense will keep running normally in the background.";
    }

    /// <summary>
    /// Called by the main window when it is shown, hidden, minimized or restored.
    /// <paramref name="windowAway"/> is true when the window is hidden or minimized.
    /// </summary>
    internal void SetBackgroundSleep(bool windowAway)
    {
        bool asleep = windowAway && !_disposed && IsSleepInBackgroundEnabled;
        if (asleep == _backgroundAsleep) return;
        _backgroundAsleep = asleep;

        try { (EfficiencyModeSetter ?? SetProcessEfficiencyMode)(asleep); }
        catch (Exception ex) { AppLogger.Error("[Background sleep] " + ex.Message); }

        if (_igpuOnlyTimer is { } timer) timer.Interval = IgpuOnlyRecheckInterval;
        if (asleep)
        {
            // Telemetry reopens NVML when the window comes back; keeping it open can hold the RTX awake.
            // Off the UI thread: Shutdown waits for any NVML call in progress, which can take a moment.
            _ = System.Threading.Tasks.Task.Run(() => { try { PreySense.Gpu.NvmlHelper.Shutdown(); } catch { } });
        }
        AppLogger.Log(asleep ? "[Background sleep] Sleeping." : "[Background sleep] Awake.");
    }

    private static void SetProcessEfficiencyMode(bool on)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = 1,
            // When off, hand both decisions back to Windows instead of forcing high performance.
            ControlMask = on ? ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution : 0,
            StateMask = on ? ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution : 0,
        };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
            AppLogger.Log($"[Background sleep] Windows refused efficiency mode (error {Marshal.GetLastWin32Error()}).");
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;
    private const uint ThrottleIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref ProcessPowerThrottlingState information, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
