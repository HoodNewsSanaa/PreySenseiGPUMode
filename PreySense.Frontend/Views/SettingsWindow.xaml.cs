using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PreySense.Frontend.Views
{
    public partial class SettingsWindow : Window
    {
        private bool _openingTuningSettings;

        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (DataContext is ViewModels.MainViewModel vm)
                    await vm.RefreshStartupPreferenceAsync();
            };
            IsVisibleChanged += async (_, _) =>
            {
                if (!IsVisible) return;
                UpdateUsbChargingLabel();
                LanguageSelector.SelectedIndex = Services.LocalizationService.LanguageIndex;
                if (DataContext is ViewModels.MainViewModel vm)
                    await vm.RefreshStartupPreferenceAsync();
            };
            UpdateUsbChargingLabel();
            LanguageSelector.SelectedIndex = Services.LocalizationService.LanguageIndex;
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            SettingsScroll.MaxHeight = Math.Max(240, MaxHeight - 56);
        }

        private async void BtnOpenTuningSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_openingTuningSettings || DataContext is not ViewModels.MainViewModel vm || !vm.IsHardwareTuningEnabled) return;
            _openingTuningSettings = true;
            try
            {
                await vm.RefreshProchotLockStateAsync();
                if (!IsLoaded || !IsVisible || !vm.IsHardwareTuningEnabled) return;
                bool previousSuppression = App.SuppressAutoHide;
                App.SuppressAutoHide = true;
                try
                {
                    new TuningSettingsWindow { Owner = this, DataContext = vm }.ShowDialog();
                }
                finally { App.SuppressAutoHide = previousSuppression; }
            }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"Could not open tuning settings: {ex}"); }
            finally { _openingTuningSettings = false; }
        }

        // iGPU Only fork: live power readings (sampled only while the window is open).
        // iGPU Only fork: check this fork's releases now and offer to install a newer build.
        private async void BtnCheckForkUpdates_Click(object sender, RoutedEventArgs e)
        {
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try { await Services.ForkUpdateService.CheckNowAsync(); }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"Update check failed: {ex}"); }
            finally { App.SuppressAutoHide = previousSuppression; }
        }

        private void BtnRtxGuardUndo_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try
            {
                var apps = vm.RtxGuardMovedApps;
                if (apps.Count == 0)
                {
                    ShowMoveOffRtxMessage(Services.LocalizationService.Translate("RTX guard hasn't moved any apps."), showCancel: false, title: "RTX guard");
                    return;
                }
                string list = string.Join(", ", System.Linq.Enumerable.Select(apps, System.IO.Path.GetFileNameWithoutExtension));
                string question = Services.LocalizationService.Format(
                    "RTX guard set these apps to Power saving in Windows' graphics settings: {0}. Give them back their previous setting?", list);
                if (!ShowMoveOffRtxMessage(question, showCancel: true, confirm: "Restore", title: "RTX guard")) return;
                int count = vm.UndoRtxGuardMovedApps();
                ShowMoveOffRtxMessage(Services.LocalizationService.Format("Done: {0} apps restored.", count), showCancel: false, title: "RTX guard");
            }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"RTX guard undo failed: {ex}"); }
            finally { App.SuppressAutoHide = previousSuppression; }
        }

        private void BtnBatteryWidget_Click(object sender, RoutedEventArgs e)
        {
            try { BatteryWidgetWindow.Toggle(); }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"Battery widget failed: {ex}"); }
        }

        private async void BtnMoveOffRtx_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try
            {
                var (problem, apps) = await vm.CheckAppsOnDiscreteGpuAsync();
                if (problem != null)
                {
                    ShowMoveOffRtxMessage(problem, showCancel: false);
                    return;
                }
                string question = Services.LocalizationService.Format(
                    "These apps are using the RTX: {0}. PreySense will restart the RTX so they move to the integrated GPU. Most apps carry on; games and some apps may close, so save your work first. Continue?",
                    string.Join(", ", apps));
                if (!ShowMoveOffRtxMessage(question, showCancel: true)) return;
                string result = await vm.MoveAppsOffDiscreteGpuAsync();
                ShowMoveOffRtxMessage(Services.LocalizationService.Translate(result), showCancel: false);
            }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"Move apps off the RTX failed: {ex}"); }
            finally { App.SuppressAutoHide = previousSuppression; }
        }

        private bool ShowMoveOffRtxMessage(string message, bool showCancel, string confirm = "Move", string title = "Move apps off the RTX")
        {
            // The Settings window may have been closed while the RTX was restarting.
            Window? owner = IsLoaded && IsVisible ? this
                : Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
            var dialog = new CustomModalDialog
            {
                Owner = owner,
                Title = "PreySense — " + Services.LocalizationService.Translate(title),
                HeaderSubtitle = " — " + Services.LocalizationService.Translate(title),
                Message = Services.LocalizationService.Translate(message),
                ConfirmText = Services.LocalizationService.Translate(showCancel ? confirm : "OK"),
                ShowCancelButton = showCancel
            };
            if (showCancel) dialog.CancelText = Services.LocalizationService.Translate("Cancel");
            return dialog.ShowDialog() == true;
        }

        private void BtnOpenPowerDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try
            {
                new PowerDiagnosticsWindow { Owner = this }.ShowDialog();
            }
            catch (System.Exception ex) { PreySense.AppLogger.Error($"Could not open power diagnostics: {ex}"); }
            finally { App.SuppressAutoHide = previousSuppression; }
        }

        private void BtnOpenLightingModeSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;
            bool previousSuppression = App.SuppressAutoHide;
            App.SuppressAutoHide = true;
            try
            {
                new LightingSettingsWindow { Owner = this, DataContext = vm }.ShowDialog();
            }
            finally { App.SuppressAutoHide = previousSuppression; }
        }

        private void UsbCharging_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateUsbChargingLabel();
        }

        private void UpdateUsbChargingLabel()
        {
            UsbChargingSlider.ValueText = UsbChargingSlider.Value == 0
                ? Services.LocalizationService.Translate("Disabled") : $"{UsbChargingSlider.Value:0}%";
        }

        private async void UsbCharging_MouseReleased(object sender, MouseButtonEventArgs e) => await CommitUsbChargingAsync();
        private async void UsbCharging_MouseCaptureLost(object sender, MouseEventArgs e) => await CommitUsbChargingAsync();
        private async void UsbCharging_KeyReleased(object sender, KeyEventArgs e) => await CommitUsbChargingAsync();

        private async System.Threading.Tasks.Task CommitUsbChargingAsync()
        {
            if (DataContext is ViewModels.MainViewModel vm)
                await vm.SetUsbChargingAsync((int)UsbChargingSlider.Value);
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageSelector.SelectedItem is ComboBoxItem item && item.Tag is string language)
            {
                Services.LocalizationService.SetLanguage(language);
                UpdateUsbChargingLabel();
            }
        }

    }
}
