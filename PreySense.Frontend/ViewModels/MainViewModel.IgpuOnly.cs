using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Core.Configuration;
using PreySense.Core.Services;
using PreySense.Gpu;

namespace PreySense.Frontend.ViewModels
{
    /// <summary>
    /// iGPU Only: the screen runs on the integrated GPU and the NVIDIA GPU is switched off in Windows
    /// (Device Manager disable), so no app can render on it. Selecting any other GPU mode switches it
    /// back on first. Optionally applied automatically on battery.
    ///
    /// Safety rules:
    ///  - never switched off while the NVIDIA GPU drives a screen (BIOS dGPU Only, Advanced Optimus,
    ///    or an external monitor on an NVIDIA-wired port) or while a game / non-desktop app uses it;
    ///  - switched back on before a BIOS GPU change, on Quit, and when Windows signs out or shuts down,
    ///    so Windows never boots with it disabled.
    /// </summary>
    public partial class MainViewModel
    {
        public const int IgpuOnlyMode = 3;
        private const int IgpuOnlyRecheckSeconds = 20;

        // Desktop apps that may sit on the NVIDIA GPU without needing it. Switching it off is fine while they
        // use it: they recover on the integrated GPU. Apps under C:\Windows and Store apps are treated the same
        // way by NvidiaGpuControl.GetActiveUserAppNames.
        private static readonly HashSet<string> IgpuOnlyHarmlessApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "brave", "msedge", "msedgewebview2", "chrome", "firefox", "opera", "vivaldi",
            "discord", "spotify", "code", "teams", "ms-teams", "slack", "whatsapp", "obsidian", "notion",
            "windowsterminal", "powershell", "pwsh", "cmd", "onedrive", "widgets", "phoneexperiencehost",
            "nvidia app", "nvidia overlay", "nvcontainer", "vlc", "mpc-hc64", "mpc-be64", "mpv",
            "preysense", "preysense.overlay", "claude"
        };

        private SemaphoreSlim? _igpuOnlyLock;
        private SemaphoreSlim IgpuOnlyLock => LazyInitializer.EnsureInitialized(ref _igpuOnlyLock, () => new SemaphoreSlim(1, 1));
        private System.Windows.Threading.DispatcherTimer? _igpuOnlyTimer;
        private int _igpuOnlyTimerCreated;
        private volatile bool _igpuOnlyRtxOff;
        // Set before a BIOS GPU change and at exit: from then on the NVIDIA GPU is never switched off again.
        private volatile bool _igpuOnlySuspended;
        private string _igpuOnlyLastNotice = string.Empty;

        public bool IsGpuIgpuOnlyActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == IgpuOnlyMode;

        private static SessionConfig ReadSessionConfig()
        {
            try { return ConfigurationService.Instance.GetCurrent().Session; }
            catch { return new SessionConfig(); }
        }

        private static void SaveIgpuOnlyPreference(bool enabled)
        {
            try { ConfigurationService.Instance.Update(cfg => cfg.Session.IgpuOnly = enabled); }
            catch (Exception ex) { AppLogger.Error($"Could not save the iGPU Only setting: {ex.Message}"); }
        }

        /// <summary>True when this hardware supports switching the NVIDIA GPU off (an NVIDIA adapter exists).</summary>
        private async Task<bool> IsIgpuOnlyAvailableAsync()
        {
            if (_hw is null || _hw is MockHardwareService) return false;
            try { return await Task.Run(() => _hw.IsDiscreteGpuDisabled()) is not null; }
            catch { return false; }
        }

        /// <summary>True when the NVIDIA GPU should be off right now (iGPU Only selected, or on battery).</summary>
        private bool WantsIgpuOnly()
        {
            if (_igpuOnlySuspended) return false;
            var session = ReadSessionConfig();
            if (session.IgpuOnly) return true;
            return session.IgpuOnlyOnBattery && _currentPowerSource == PowerSourceKind.Battery;
        }

        private void IgpuOnlyNotice(string message)
        {
            if (message == _igpuOnlyLastNotice) return;
            _igpuOnlyLastNotice = message;
            DiagnosticMessage = message;
        }

        private void ShowIgpuOnlyState()
        {
            ActiveGpuMuxMode = IgpuOnlyMode;
            GpuMuxModeName = "iGPU Only";
            NotifyGpuSelectionsChanged();
        }

        /// <summary>Must run on the UI thread (EnforceIgpuOnlyAsync guarantees it).</summary>
        private void EnsureIgpuOnlyTimer()
        {
            if (_disposed || _dispatcher == null || Interlocked.Exchange(ref _igpuOnlyTimerCreated, 1) != 0) return;
            _igpuOnlyTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(IgpuOnlyRecheckSeconds),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) =>
                {
                    if (!_disposed && (_igpuOnlyRtxOff || WantsIgpuOnly()))
                        _ = EnforceIgpuOnlyAsync("recheck");
                },
                _dispatcher);
            _igpuOnlyTimer.Start();
        }

        /// <summary>
        /// Brings the NVIDIA GPU state in line with the iGPU Only setting and the power source.
        /// Background rechecks skip if another iGPU Only operation is running; other callers wait for it.
        /// </summary>
        internal async Task EnforceIgpuOnlyAsync(string reason)
        {
            if (_hw is null || _hw is MockHardwareService) return;
            // Run on the UI thread, like a button click, whatever thread the trigger came from.
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                if (_disposed) return;
                await _dispatcher.InvokeAsync<Task>(() => EnforceIgpuOnlyAsync(reason)).Task.Unwrap();
                return;
            }
            if (_disposed || _igpuOnlySuspended || !await IsIgpuOnlyAvailableAsync()) return;
            EnsureIgpuOnlyTimer();

            bool background = reason == "recheck";
            if (background ? !IgpuOnlyLock.Wait(0) : !await IgpuOnlyLock.WaitAsync(TimeSpan.FromSeconds(60)))
                return;
            try
            {
                if (_disposed || _igpuOnlySuspended) return;
                bool want = WantsIgpuOnly();
                bool? disabled = await Task.Run(() => _hw.IsDiscreteGpuDisabled());
                if (disabled is null) return;
                _igpuOnlyRtxOff = disabled == true;

                if (want && disabled == true)
                {
                    ShowIgpuOnlyState();
                }
                else if (want)
                {
                    await SwitchDiscreteGpuOffAsync(reason);
                }
                else if (disabled == true)
                {
                    // Not wanted any more (plugged in, or the RTX was left off): switch it back on and
                    // return to the saved GPU mode.
                    if (await SwitchDiscreteGpuOnAsync(reason))
                    {
                        int preference = Math.Clamp(ReadSessionConfig().GpuModePreference, 0, 2);
                        if (preference != 0)
                            await ApplyGpuMuxModeCoreAsync(preference, persistPreference: false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"iGPU Only ({reason}) failed: {ex}");
            }
            finally
            {
                IgpuOnlyLock.Release();
            }
        }

        private async Task<bool> IsDiscreteGpuDrivingScreenAsync()
        {
            if (IsBiosDiscreteGpuActive) return true;
            return await Task.Run(() =>
            {
                try
                {
                    if (_hw.IsBiosDiscreteGpuActive) return true;
                    // Unknown counts as "yes": never risk switching off the GPU behind a screen.
                    return _hw.IsScreenOnDiscreteGpu() != false;
                }
                catch { return true; }
            });
        }

        private static bool IsScreenOnIntel(DdsStatus? status) =>
            status is { IsSupported: true, KmdResponse: 0, IsAutomatic: false, ActiveMux: DdsActiveMux.Optimus };

        /// <summary>Must be called while holding IgpuOnlyLock.</summary>
        private async Task SwitchDiscreteGpuOffAsync(string reason)
        {
            if (System.Threading.Volatile.Read(ref _gpuSwitchInProgress) != 0) return; // a GPU switch is running; retried later
            if (IsBiosDiscreteGpuActive || await Task.Run(() => _hw.IsBiosDiscreteGpuActive))
            {
                IgpuOnlyNotice("iGPU Only is not available while dGPU Only is set.");
                return;
            }

            // 1. Don't pull the GPU out from under a game or other real GPU work. Checked before any screen
            //    switch, so a running game is never interrupted. (NVIDIA's own "blocking app" flag is not used:
            //    it is always set while the screen is on the NVIDIA GPU.)
            var apps = await Task.Run(() => _hw.GetDiscreteGpuAppNames());
            if (apps is null)
            {
                IgpuOnlyNotice("iGPU Only: can't read which apps use the RTX right now; trying again shortly.");
                return;
            }
            var blockers = apps.Where(a => !IgpuOnlyHarmlessApps.Contains(a))
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (blockers.Count > 0)
            {
                IgpuOnlyNotice("iGPU Only: the RTX switches off when you close " + string.Join(", ", blockers) + ".");
                return;
            }

            // 2. Screen to the integrated GPU (from Advanced Optimus / Auto).
            var status = await Task.Run(() => _hw.IsDdsAvailable ? _hw.GetDdsStatus() : null);
            if (status?.IsSupported == true && !IsScreenOnIntel(status))
            {
                await ApplyGpuMuxModeCoreAsync(0, persistPreference: false);
                status = await Task.Run(() => _hw.IsDdsAvailable ? _hw.GetDdsStatus() : null);
                if (!IsScreenOnIntel(status))
                {
                    IgpuOnlyNotice("iGPU Only: waiting for the screen to switch to the integrated GPU.");
                    return;
                }
                await Task.Delay(2000); // let Windows finish moving the desktop before checking the outputs
            }

            // 3. Never switch off the GPU behind a screen (an external monitor on an NVIDIA-wired port, or
            //    when the screen can't be confirmed on the integrated GPU).
            if (await IsDiscreteGpuDrivingScreenAsync())
            {
                IgpuOnlyNotice("iGPU Only: the RTX stays on while a screen is connected to it.");
                return;
            }

            // 4. Switch it off (re-checked under the hardware gate in case anything changed meanwhile).
            bool ok = false;
            bool markedBusy = false;
            try
            {
                using (await HardwareOperationGate.EnterDisplaySwitchAsync())
                {
                    if (!WantsIgpuOnly() || _disposed || _igpuOnlySuspended || IsBiosDiscreteGpuActive) return;
                    if (!IsBusy) { IsBusy = true; markedBusy = true; }
                    ok = await Task.Run(() => _hw.SetDiscreteGpuEnabled(false));
                }
            }
            catch (TimeoutException)
            {
                return; // a sensor read held the hardware; retried later
            }
            finally
            {
                if (markedBusy) IsBusy = false;
            }
            if (!ok)
            {
                IgpuOnlyNotice("iGPU Only: Windows could not switch the RTX off.");
                return;
            }
            _igpuOnlyRtxOff = true;
            ShowIgpuOnlyState();
            IgpuOnlyNotice(reason == "power" ? "On battery: iGPU Only, RTX switched off." : "iGPU Only: RTX switched off.");
        }

        /// <summary>Switches the NVIDIA GPU back on and waits for its display-switch API. Never throws.</summary>
        private async Task<bool> SwitchDiscreteGpuOnAsync(string reason)
        {
            bool ok = false;
            bool markedBusy = false;
            try
            {
                DiagnosticMessage = "Switching the RTX on...";
                using (await HardwareOperationGate.EnterDisplaySwitchAsync())
                {
                    if (!IsBusy) { IsBusy = true; markedBusy = true; }
                    ok = await Task.Run(() => _hw.SetDiscreteGpuEnabled(true));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Could not switch the RTX on: {ex.Message}");
                ok = false;
            }
            finally
            {
                if (markedBusy) IsBusy = false;
            }
            if (!ok)
            {
                _igpuOnlyLastNotice = string.Empty;
                IgpuOnlyNotice("Could not switch the RTX on. Try again, or enable it in Device Manager.");
                return false;
            }
            _igpuOnlyRtxOff = false;
            // Wait (up to 10 s) for NVIDIA's display-switch API to come back before anything uses it.
            for (int i = 0; i < 40; i++)
            {
                var status = await Task.Run(() => _hw.IsDdsAvailable ? _hw.GetDdsStatus() : null);
                if (status?.IsSupported == true) { ApplyDdsStatus(status); break; }
                await Task.Delay(250);
            }
            try { await Task.Run(_hw.RefreshGpuAdapter); } catch { }
            _igpuOnlyLastNotice = string.Empty;
            IgpuOnlyNotice(reason == "power" ? "Plugged in: RTX available again." : "RTX switched on.");
            return true;
        }

        /// <summary>
        /// Called when the user picks a GPU mode button. Returns true when the stock GPU switch should run next.
        /// iGPU Only is handled here completely.
        /// </summary>
        private async Task<bool> PrepareGpuModeSelectionAsync(int muxVal)
        {
            if (!await IsIgpuOnlyAvailableAsync())
            {
                if (muxVal == IgpuOnlyMode)
                {
                    DiagnosticMessage = "iGPU Only is not available on this system.";
                    return false;
                }
                return true; // stock behaviour
            }

            _igpuOnlyLastNotice = string.Empty;
            if (muxVal == IgpuOnlyMode)
            {
                SaveIgpuOnlyPreference(true);
                await EnforceIgpuOnlyAsync("user");
                return false;
            }

            // Any other mode: leave iGPU Only, and switch the NVIDIA GPU on before the stock switch.
            SaveIgpuOnlyPreference(false);
            if (WantsIgpuOnly())
            {
                SaveGpuModePreference(muxVal);
                DiagnosticMessage = $"On battery the RTX stays off. {GetGpuModeName(muxVal)} is used when you plug in.";
                return false;
            }
            if (!await IgpuOnlyLock.WaitAsync(TimeSpan.FromSeconds(60)))
            {
                DiagnosticMessage = "The RTX is still being switched. Try again in a moment.";
                return false;
            }
            try
            {
                bool? disabled = await Task.Run(() => _hw.IsDiscreteGpuDisabled());
                if (disabled == true && !await SwitchDiscreteGpuOnAsync("mode"))
                    return false;
                return true;
            }
            finally
            {
                IgpuOnlyLock.Release();
            }
        }

        /// <summary>Before a BIOS GPU change (needs a restart): switch the NVIDIA GPU on and keep it on.</summary>
        private async Task<bool> PrepareBiosGpuChangeAsync()
        {
            _igpuOnlySuspended = true; // stops all further switch-offs this session
            SaveIgpuOnlyPreference(false);
            if (!await IsIgpuOnlyAvailableAsync()) return true;
            if (!await IgpuOnlyLock.WaitAsync(TimeSpan.FromSeconds(60)))
            {
                _igpuOnlySuspended = false;
                return false;
            }
            try
            {
                bool? disabled = await Task.Run(() => _hw.IsDiscreteGpuDisabled());
                bool ok = disabled != true || await SwitchDiscreteGpuOnAsync("bios");
                if (!ok) _igpuOnlySuspended = false;
                return ok;
            }
            finally
            {
                IgpuOnlyLock.Release();
            }
        }

        /// <summary>
        /// At Quit and at Windows sign-out/shutdown: switch the NVIDIA GPU back on so Windows never starts
        /// with it disabled. PreySense switches it off again at the next start if iGPU Only applies.
        /// </summary>
        private void RestoreDiscreteGpuForExit(string why)
        {
            _igpuOnlySuspended = true;
            try { _igpuOnlyTimer?.Stop(); } catch { }
            if (_hw is null || _hw is MockHardwareService) return;
            try
            {
                if (_hw.IsDiscreteGpuDisabled() != true) return;
                AppLogger.Log($"iGPU Only: switching the RTX back on ({why}).");
                // Bounded wait: the enable request is issued immediately; only the confirmation is waited on.
                Task.Run(() => _hw.SetDiscreteGpuEnabled(true)).Wait(TimeSpan.FromSeconds(why == "shutdown" ? 4 : 12));
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Could not switch the RTX back on ({why}): {ex.Message}");
            }
        }
    }
}
