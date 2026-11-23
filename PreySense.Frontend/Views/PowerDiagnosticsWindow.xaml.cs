using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PreySense.Core.Hardware;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.Views
{
    /// <summary>
    /// iGPU Only fork: battery drain, CPU package power, package C-state residency and RTX power state.
    /// Samples once a second while open; nothing is read while it is closed.
    /// </summary>
    public partial class PowerDiagnosticsWindow : Window
    {
        public sealed record CStateRow(string Name, double Percent, string Display);

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly ObservableCollection<CStateRow> _cStates = new();
        private PowerDiagnosticsSampler? _sampler;
        private bool _sampling;
        private bool _closed;

        public PowerDiagnosticsWindow()
        {
            InitializeComponent();
            CStateList.ItemsSource = _cStates;
            _timer.Tick += async (_, _) => await SampleAsync();
            Loaded += async (_, _) =>
            {
                await SampleAsync(); // primes the counters; the first deltas arrive one second later
                if (!_closed) _timer.Start();
            };
            Closed += (_, _) =>
            {
                _closed = true;
                _timer.Stop();
            };
        }

        private async Task SampleAsync()
        {
            if (_sampling || _closed) return;
            _sampling = true;
            try
            {
                var snapshot = await Task.Run(() =>
                {
                    _sampler ??= new PowerDiagnosticsSampler(CpuDriverRuntime.Backend);
                    return _sampler.Sample();
                });
                if (!_closed) Render(snapshot);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[Power diagnostics] " + ex.Message);
            }
            finally { _sampling = false; }
        }

        private void Render(PowerDiagnosticsSnapshot s)
        {
            string percent = s.BatteryPercent is int p ? $" ({p}%)" : string.Empty;
            ValueSource.Text = s.OnAcPower switch
            {
                true => LocalizationService.Translate("Plugged in") + percent,
                false => LocalizationService.Translate("Battery") + percent,
                _ => "—"
            };
            ValueBattery.Text = s.BatteryDischargeWatts is > 0.05 and double drain
                ? Watts(drain)
                : s.BatteryChargeWatts is > 0.05 and double charge
                    ? LocalizationService.Format("Charging {0}", Watts(charge))
                    : "—";
            ValuePackage.Text = s.CpuPackageWatts is double cpu ? Watts(cpu) : "—";
            ValueRest.Text = s.OnAcPower != true && s.RestOfSystemWatts is double rest ? Watts(rest) : "—";
            ValueRtx.Text = LocalizationService.Translate(
                PowerDiagnosticsSampler.DescribeGpuState(s.DiscreteGpuPowerState, s.DiscreteGpuDisabled));

            CStateUnavailable.Visibility = s.PackageCStatesSupported ? Visibility.Collapsed : Visibility.Visible;
            CStateList.Visibility = s.PackageCStatesSupported ? Visibility.Visible : Visibility.Collapsed;
            if (!s.PackageCStatesSupported) return;
            for (int i = 0; i < s.PackageCStates.Count; i++)
            {
                var state = s.PackageCStates[i];
                var row = new CStateRow(state.Name, state.Percent ?? 0,
                    state.Percent is double value ? value.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "—");
                if (i < _cStates.Count) _cStates[i] = row;
                else _cStates.Add(row);
            }
        }

        private static string Watts(double watts) => watts.ToString("0.0", CultureInfo.CurrentCulture) + " W";
    }
}
