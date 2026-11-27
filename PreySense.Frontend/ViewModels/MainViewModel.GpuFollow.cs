using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PreySense.Core.Configuration;
using PreySense.Frontend.Services;
using PreySense.Gpu;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: one GPU at a time. Running an app on one GPU while the other drives the screen means copying every
/// frame across, which feels slower. So when the GPU tile changes:
/// <list type="bullet">
/// <item>NVIDIA's preferred GPU follows the screen: NVIDIA while the RTX drives it (Advanced Optimus, dGPU Only),
/// otherwise the mode's own Preferred GPU setting;</item>
/// <item>Power saving choices PreySense made are taken back while the RTX drives the screen, and put back after;</item>
/// <item>apps still running on the other GPU are listed, and PreySense offers to restart them so they move.</item>
/// </list>
/// </summary>
public partial class MainViewModel
{
    /// <summary>True when the GPU tile puts the screen on the RTX (Advanced Optimus or BIOS dGPU Only).</summary>
    private bool ScreenOnRtxSelected()
    {
        try { return IsBiosDiscreteGpuActive || ConfigurationService.Instance.GetCurrent().Session.GpuModePreference == 2; }
        catch { return false; }
    }

    /// <summary>The preferred GPU to use now: NVIDIA while the RTX drives the screen, else the mode's own setting.</summary>
    internal int? EffectivePreferredGpu(TuningConfig tuning, byte mode)
    {
        if (TuningConfigurationReader is null && _dispatcher is not null && ScreenOnRtxSelected())
            return (int)NvidiaPreferredGpu.Nvidia;
        return ProfileForMode(tuning, mode).PreferredGpu;
    }

    private int _gpuFollowGeneration;

    /// <summary>Called after a GPU tile switch: re-applies what follows the GPU, then offers to move stragglers.</summary>
    private void OnGpuModeSwitched()
    {
        byte mode = ActivePowerMode;
        int generation = Interlocked.Increment(ref _gpuFollowGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                ApplyPreferredGpuForMode(mode);
                ApplyRtxGuardForMode(mode);
                await Task.Delay(TimeSpan.FromSeconds(4)); // let the switch settle and apps redraw
                if (generation != Volatile.Read(ref _gpuFollowGeneration) || _disposed) return;
                await OfferToMoveStragglersAsync();
            }
            catch (Exception ex) { AppLogger.Error("[GPU follow] " + ex.Message); }
        });
    }

    private async Task OfferToMoveStragglersAsync()
    {
        bool toRtx = ScreenOnRtxSelected();
        if (!toRtx && ConfigurationService.Instance.GetCurrent().Session.GpuModePreference != 0) return; // Auto moves by itself
        var nvidia = RtxGuard.NvidiaAdapterLuids();
        if (nvidia.Count == 0) return;
        var pairs = RtxGuard.ProcessesByAdapter();
        if (pairs is null) return;

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var onTarget = new HashSet<int>(pairs.Where(p => nvidia.Contains(p.Luid) == toRtx).Select(p => p.Pid));
        var stragglers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // exe path → name
        foreach (var (pid, luid) in pairs)
        {
            if (nvidia.Contains(luid) == toRtx || onTarget.Contains(pid)) continue; // on the right GPU
            string? path = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.Id == Environment.ProcessId || process.SessionId == 0) continue;
                path = process.MainModule?.FileName;
            }
            catch { }
            if (!IsMovableApp(path, windowsDir)) continue;
            stragglers[path!] = Path.GetFileNameWithoutExtension(path!);
        }
        if (stragglers.Count == 0) return;

        string gpuName = LocalizationService.Translate(toRtx ? "the RTX" : "the integrated GPU");
        string otherName = LocalizationService.Translate(toRtx ? "the integrated GPU" : "the RTX");
        string message = LocalizationService.Format(
            "These apps are still running on {0}: {1}. Restart them so they move to {2}? They will close and open again, so save your work first.",
            otherName, string.Join(", ", stragglers.Values.Distinct(StringComparer.OrdinalIgnoreCase)), gpuName);
        if (!await AskOnUiAsync(message, "Restart apps")) return;

        var notRestarted = new List<string>();
        foreach (var (path, name) in stragglers)
            if (!await Task.Run(() => RestartApp(path))) notRestarted.Add(name);
        SetDiagnosticFromAnyThread(notRestarted.Count == 0
            ? LocalizationService.Format("Apps restarted on {0}.", gpuName)
            : LocalizationService.Format("Close these from their tray icon to move them: {0}.", string.Join(", ", notRestarted)));
    }

    /// <summary>Closes every window of the app, waits for it to exit, then starts it again as the signed-in user.</summary>
    private static bool RestartApp(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        List<Process> processes = Process.GetProcessesByName(name).Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToList();
        if (processes.Count == 0) return RtxGuard.LaunchAsUser(exePath);
        var pids = new HashSet<uint>(processes.Select(p => (uint)p.Id));
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pids.Contains(pid) && IsWindowVisible(hwnd)) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        foreach (var p in processes)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !p.WaitForExit((int)left.TotalMilliseconds)) return false; // still running (tray)
            }
            catch { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(500);
        return RtxGuard.LaunchAsUser(exePath);
    }

    private Task<bool> AskOnUiAsync(string message, string confirm)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null) return Task.FromResult(false);
        return app.Dispatcher.InvokeAsync(() =>
        {
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try
            {
                var owner = app.MainWindow is { IsVisible: true } main && main.WindowState != WindowState.Minimized ? main : null;
                var dialog = new Views.CustomModalDialog
                {
                    Owner = owner,
                    Topmost = owner is null,
                    WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                    Title = "PreySense — " + LocalizationService.Translate("Move apps"),
                    HeaderSubtitle = " — " + LocalizationService.Translate("Move apps"),
                    Message = message,
                    ConfirmText = LocalizationService.Translate(confirm),
                    CancelText = LocalizationService.Translate("Not now"),
                    ShowCancelButton = true
                };
                return dialog.ShowDialog() == true;
            }
            finally { App.SuppressAutoHide = previousSuppression; }
        }).Task;
    }

    private const uint WmClose = 0x0010;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
