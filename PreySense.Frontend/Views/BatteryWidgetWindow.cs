using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PreySense.Core.Hardware;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.Views
{
    /// <summary>
    /// iGPU Only fork (temporary): a small always-on-top battery readout. Shows the drain now and averaged over
    /// the last 30 seconds, battery percent and estimated time left, CPU package power and whether the RTX is
    /// awake. Reads every 2 seconds while open, nothing while closed; reading never wakes the RTX.
    /// Drag to move; double-click or right-click to close.
    /// </summary>
    public sealed class BatteryWidgetWindow : Window
    {
        private static BatteryWidgetWindow? _current;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly Queue<(DateTime Time, double Watts)> _history = new();
        private readonly TextBlock _line1 = Line(15, FontWeights.SemiBold);
        private readonly TextBlock _line2 = Line(12, FontWeights.Normal);
        private readonly TextBlock _line3 = Line(12, FontWeights.Normal);
        private PowerDiagnosticsSampler? _sampler;
        private bool _sampling;
        private bool _closed;

        public static bool IsOpen => _current is { IsVisible: true };

        /// <summary>Opens the widget, or closes it when it is already open.</summary>
        public static void Toggle()
        {
            if (_current is { } open) { open.Close(); return; }
            _current = new BatteryWidgetWindow();
            _current.Show();
        }

        private BatteryWidgetWindow()
        {
            Title = "PreySense battery";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ToolTip = LocalizationService.Translate("Drag to move. Double-click or right-click to close.");

            var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            panel.Children.Add(_line1);
            panel.Children.Add(_line2);
            panel.Children.Add(_line3);
            Content = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x14, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel
            };
            _line1.Text = LocalizationService.Translate("Reading battery...");

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount >= 2) { Close(); return; }
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            MouseRightButtonUp += (_, _) => Close();

            _timer.Tick += async (_, _) => await SampleAsync();
            Loaded += async (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 16;
                Top = area.Top + 16;
                await SampleAsync();
                if (!_closed) _timer.Start();
            };
            Closed += (_, _) =>
            {
                _closed = true;
                _timer.Stop();
                if (ReferenceEquals(_current, this)) _current = null;
            };
        }

        private static TextBlock Line(double size, FontWeight weight) => new()
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 1, 0, 1)
        };

        private async Task SampleAsync()
        {
            if (_sampling || _closed) return;
            _sampling = true;
            try
            {
                var (snapshot, remainingWh) = await Task.Run(() =>
                {
                    _sampler ??= new PowerDiagnosticsSampler(CpuDriverRuntime.Backend);
                    return (_sampler.Sample(), ReadRemainingWh());
                });
                if (!_closed) Render(snapshot, remainingWh);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[Battery widget] " + ex.Message);
            }
            finally { _sampling = false; }
        }

        private void Render(PowerDiagnosticsSnapshot s, double? remainingWh)
        {
            var culture = CultureInfo.CurrentCulture;
            DateTime now = DateTime.UtcNow;
            bool onBattery = s.OnAcPower == false && s.BatteryDischargeWatts is > 0.05;
            if (onBattery) _history.Enqueue((now, s.BatteryDischargeWatts!.Value));
            else _history.Clear();
            while (_history.Count > 0 && now - _history.Peek().Time > TimeSpan.FromSeconds(30)) _history.Dequeue();
            double? average = _history.Count > 0 ? _history.Average(h => h.Watts) : null;

            if (onBattery)
            {
                _line1.Text = LocalizationService.Format("{0} W now · {1} W avg",
                    s.BatteryDischargeWatts!.Value.ToString("0.0", culture),
                    (average ?? s.BatteryDischargeWatts.Value).ToString("0.0", culture));
            }
            else if (s.BatteryChargeWatts is > 0.05 and double charge)
            {
                _line1.Text = LocalizationService.Format("Charging {0}", charge.ToString("0.0", culture) + " W");
            }
            else
            {
                _line1.Text = LocalizationService.Translate("Plugged in");
            }

            string percent = s.BatteryPercent is int p ? p + "%" : "—";
            string left = onBattery && remainingWh is > 0 and double wh && average is > 0.5 and double avg
                ? " · " + LocalizationService.Format("~{0} left", FormatHours(wh / avg))
                : string.Empty;
            _line2.Text = percent + left;

            string cpu = s.CpuPackageWatts is double c ? c.ToString("0.0", culture) + " W" : "—";
            string rtx = s.DiscreteGpuPowerState switch
            {
                1 => LocalizationService.Translate("awake"),
                2 or 3 or 4 => LocalizationService.Translate("asleep"),
                _ => s.DiscreteGpuDisabled == true ? LocalizationService.Translate("off") : "—"
            };
            _line3.Text = LocalizationService.Format("CPU {0} · RTX {1}", cpu, rtx);
            _line3.Foreground = s.DiscreteGpuPowerState == 1
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40))
                : Brushes.White;
        }

        private static string FormatHours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0) return "—";
            hours = Math.Min(hours, 99);
            int h = (int)hours;
            int m = (int)Math.Round((hours - h) * 60);
            if (m == 60) { h++; m = 0; }
            return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
        }

        /// <summary>Remaining battery energy in Wh (all batteries), or null when Windows doesn't report it.</summary>
        private static double? ReadRemainingWh()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT RemainingCapacity FROM BatteryStatus");
                double total = 0;
                bool any = false;
                foreach (ManagementObject battery in searcher.Get())
                {
                    using (battery)
                    {
                        if (battery["RemainingCapacity"] is uint mwh) { total += mwh / 1000.0; any = true; }
                    }
                }
                return any ? total : null;
            }
            catch { return null; }
        }
    }
}
