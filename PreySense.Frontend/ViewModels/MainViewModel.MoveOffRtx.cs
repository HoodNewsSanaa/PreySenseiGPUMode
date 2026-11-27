using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Core.Services;

namespace PreySense.Frontend.ViewModels;

/// <summary>
/// iGPU Only fork: "Move apps off the RTX". Restarts the RTX's driver (switch off, switch on) so every app using
/// it loses its connection. Apps that handle that (browsers, Discord, most desktop apps) carry on on the
/// integrated GPU, as long as the preferred GPU sends new work there; others may close. The RTX can then sleep.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// True when Windows reports the RTX asleep (D1–D3) or switched off. Reading this never wakes it, unlike
    /// NVIDIA's own APIs, so callers use it to leave a sleeping RTX alone.
    /// </summary>
    internal bool IsRtxAsleepOrOff()
    {
        try
        {
            if (_igpuOnlyRtxOff) return true;
            if (_hw.GetDiscreteGpuPowerState() is >= 2 and <= 4) return true;
            return _hw.IsDiscreteGpuDisabled() == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks whether the RTX can be restarted and which apps use it. Problem is a user-facing reason when it
    /// can't (or needn't) be done; otherwise Apps lists the apps that would be moved.
    /// </summary>
    internal async Task<(string? Problem, IReadOnlyList<string> Apps)> CheckAppsOnDiscreteGpuAsync()
    {
        try
        {
            if (_igpuOnlyRtxOff || await Task.Run(() => _hw.IsDiscreteGpuDisabled()) == true)
                return ("The RTX is already switched off (iGPU Only).", Array.Empty<string>());
            if (await IsDiscreteGpuDrivingScreenAsync())
                return ("The RTX is driving a screen right now, so it can't be restarted. Switch the display to the integrated GPU first.", Array.Empty<string>());
            var apps = await Task.Run(() => _hw.GetDiscreteGpuAppNames());
            if (apps is null)
                return ("PreySense couldn't read which apps use the RTX. Try again in a moment.", Array.Empty<string>());
            var list = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
                return ("No apps are using the RTX, so it can already go to sleep.", Array.Empty<string>());
            return (null, list);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Move off RTX] " + ex.Message);
            return ("PreySense couldn't check the RTX: " + ex.Message, Array.Empty<string>());
        }
    }

    /// <summary>Restarts the RTX's driver and reports which apps (if any) went back to it. Never throws.</summary>
    internal async Task<string> MoveAppsOffDiscreteGpuAsync()
    {
        if (Interlocked.CompareExchange(ref _gpuSwitchInProgress, 1, 0) != 0)
            return "A GPU switch is already running. Try again when it finishes.";
        bool locked = false;
        bool markedBusy = false;
        try
        {
            locked = await IgpuOnlyLock.WaitAsync(TimeSpan.FromSeconds(30));
            if (!locked) return "iGPU Only is busy switching the RTX. Try again in a moment.";
            if (!IsBusy) { IsBusy = true; markedBusy = true; }
            DiagnosticMessage = "Restarting the RTX to move apps to the integrated GPU...";

            bool off, on;
            using (await HardwareOperationGate.EnterDisplaySwitchAsync())
            {
                off = await Task.Run(() => _hw.SetDiscreteGpuEnabled(false));
                if (off) await Task.Delay(1500);
                // Always switch it back on, even if switching off only partly worked.
                on = await Task.Run(() => _hw.SetDiscreteGpuEnabled(true));
            }
            if (!on)
                return "The RTX didn't come back on. Pick a GPU mode again, or enable it in Device Manager.";
            if (!off)
                return "Windows wouldn't switch the RTX off, so the apps stayed on it.";

            try { await Task.Run(_hw.RefreshGpuAdapter); } catch { }
            byte activeMode = ActivePowerMode;
            await Task.Run(() => ApplyPreferredGpuForMode(activeMode));

            await Task.Delay(3000); // let apps rebuild their graphics on whichever GPU they now pick
            var after = await Task.Run(() => _hw.GetDiscreteGpuAppNames());
            var back = after?.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            AppLogger.Log("[Move off RTX] RTX restarted; apps on it now: " + (back is null ? "unknown" : string.Join(", ", back)));
            if (back is { Count: > 0 })
                return PreySense.Frontend.Services.LocalizationService.Format(
                    "RTX restarted, but these apps went back to it: {0}. Set this mode's preferred GPU to Integrated graphics, or set these apps to Power saving in Windows' graphics settings.",
                    string.Join(", ", back));
            return "Done. Apps were moved off the RTX, so it can go to sleep.";
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Move off RTX] " + ex);
            return "Moving apps off the RTX failed: " + ex.Message;
        }
        finally
        {
            if (markedBusy) IsBusy = false;
            if (locked) IgpuOnlyLock.Release();
            Volatile.Write(ref _gpuSwitchInProgress, 0);
        }
    }
}
