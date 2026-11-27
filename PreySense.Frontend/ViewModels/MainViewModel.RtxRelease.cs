using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PreySense.Core.Services;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: after switching to Eco (or any mode with the RTX guard on), checks whether the RTX stays awake
/// with no app on it. That usually means Windows' desktop compositor (dwm) kept memory on the RTX after the screen
/// was on it (Auto / Advanced Optimus, a refresh rate change). PreySense then asks whether to restart the RTX, which
/// makes dwm let go so the RTX can power down. In Hybrid the screen stays on Intel throughout.
/// </summary>
public partial class MainViewModel
{
    private const byte EcoMode = 6;
    private int _rtxReleaseCheckGeneration;

    /// <summary>Test seam: answers the question instead of showing the dialog.</summary>
    internal Func<bool>? RtxReleasePrompt { get; set; }

    private void ScheduleRtxReleaseCheck(byte mode)
    {
        try
        {
            var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
            if (mode != EcoMode && ProfileForMode(tuning, mode).RtxGuard != true) return;
        }
        catch { return; }
        int generation = Interlocked.Increment(ref _rtxReleaseCheckGeneration);
        _ = Task.Run(() => RtxReleaseCheckAsync(mode, generation));
    }

    private async Task RtxReleaseCheckAsync(byte mode, int generation)
    {
        try
        {
            // Give the mode, its extras and any app that just started a few seconds; the RTX itself needs ~5 s
            // of quiet before it powers down. Two checks 10 s apart rule out a short wake.
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (!StillCurrent()) return;
            if (!await StuckAwakeWithoutAppsAsync()) return;
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!StillCurrent() || !await StuckAwakeWithoutAppsAsync()) return;

            AppLogger.Log("[RTX release] RTX awake with no apps on it after switching mode; asking to restart it.");
            bool restart = RtxReleasePrompt?.Invoke() ?? await AskToRestartRtxAsync();
            if (!restart || !StillCurrent() || !await StuckAwakeWithoutAppsAsync()) return;
            string result = _dispatcher is null
                ? await MoveAppsOffDiscreteGpuAsync()
                : await _dispatcher.InvokeAsync(MoveAppsOffDiscreteGpuAsync).Task.Unwrap();
            SetDiagnosticFromAnyThread(LocalizationService.Translate(result));
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX release] " + ex.Message);
        }

        bool StillCurrent() => !_disposed && generation == Volatile.Read(ref _rtxReleaseCheckGeneration) && ActivePowerMode == mode;
    }

    /// <summary>True when the RTX is awake (and not driving the screen) while no app is using it.</summary>
    private async Task<bool> StuckAwakeWithoutAppsAsync()
    {
        if (IsRtxAsleepOrOff()) return false;
        if (Volatile.Read(ref _gpuSwitchInProgress) != 0) return false;
        var apps = await Task.Run(() => _hw.GetDiscreteGpuAppNames());
        if (apps is null || apps.Count > 0) return false; // unknown, or a real app: not this case
        return !await IsDiscreteGpuDrivingScreenAsync();
    }

    private Task<bool> AskToRestartRtxAsync()
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
                    Title = "PreySense — " + LocalizationService.Translate("RTX still awake"),
                    HeaderSubtitle = " — " + LocalizationService.Translate("RTX still awake"),
                    Message = LocalizationService.Translate(
                        "The RTX is staying awake although no app is using it. Windows' desktop (dwm) is probably still holding memory on it from when the screen was on the RTX. Restart the RTX so it can sleep? Your screen stays on.") ,
                    ConfirmText = LocalizationService.Translate("Restart RTX"),
                    CancelText = LocalizationService.Translate("Not now"),
                    ShowCancelButton = true
                };
                return dialog.ShowDialog() == true;
            }
            finally { App.SuppressAutoHide = previousSuppression; }
        }).Task;
    }

    private void SetDiagnosticFromAnyThread(string message)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) DiagnosticMessage = message;
        else _dispatcher.BeginInvoke(() => DiagnosticMessage = message);
    }
}
