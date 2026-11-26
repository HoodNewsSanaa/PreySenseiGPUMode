using System.Reflection;
using System.Runtime.CompilerServices;
using PreySense.Core.Configuration;
using PreySense.Core.Hardware;
using PreySense.Core.Services;
using PreySense.Frontend.ViewModels;

namespace PreySense.Tests;

/// <summary>iGPU Only fork: per-mode PROCHOT override, per-mode CPU frequency cap, diagnostics helpers.</summary>
[TestClass]
public class ForkModeExtrasTests
{
    [TestMethod]
    public void ProchotOverride_WinsOverGlobal_AndOtherModesFallBackToFactory()
    {
        var tuning = new TuningConfig { ApplyProchotOffset = false, ProchotOffsetCelsius = 12, FactoryProchotOffsetCelsius = 3 };
        tuning.Eco.ProchotOffsetOverrideCelsius = 30;

        Assert.AreEqual(30, MainViewModel.ResolveModeProchotOffset(tuning, tuning.Eco));
        // Global off + an override elsewhere: restore the factory offset so Eco's value doesn't linger.
        Assert.AreEqual(3, MainViewModel.ResolveModeProchotOffset(tuning, tuning.Balanced));

        tuning.ApplyProchotOffset = true;
        Assert.AreEqual(12, MainViewModel.ResolveModeProchotOffset(tuning, tuning.Balanced));
        Assert.AreEqual(30, MainViewModel.ResolveModeProchotOffset(tuning, tuning.Eco));
    }

    [TestMethod]
    public void ProchotOverride_AbsentEverywhere_KeepsUpstreamBehaviour()
    {
        var tuning = new TuningConfig { ApplyProchotOffset = false, ProchotOffsetCelsius = 12, FactoryProchotOffsetCelsius = 3 };
        Assert.IsNull(MainViewModel.ResolveModeProchotOffset(tuning, tuning.Turbo));
        tuning.ApplyProchotOffset = true;
        Assert.AreEqual(12, MainViewModel.ResolveModeProchotOffset(tuning, tuning.Turbo));
    }

    [TestMethod]
    public void SavedModeTuning_PassesOverrideToDriverRequest()
    {
        var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var hardware = new MockHardwareService();
        typeof(MainViewModel).GetField("_hw", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, hardware);
        var config = new PreySenseConfig();
        config.Tuning.Quiet.ProchotOffsetOverrideCelsius = 25;
        vm.TuningConfigurationReader = () => config;
        int? captured = -1;
        vm.DriverTuningExecutor = request => { captured = request.ProchotOffsetCelsius; return new(true, "ok"); };
        var result = vm.ApplySavedDriverTuning(0);
        Assert.IsTrue(result.Success, result.Status);
        Assert.AreEqual(25, captured);
    }

    [TestMethod]
    public void FrequencyCap_IsWrittenForCappedMode_AndRemovedOnlyIfPreySenseSetIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseCap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config => config.Tuning.Balanced.CpuMaxFrequencyMhz = 4000);
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var writes = new List<int>();
            vm.CpuFrequencyCapWriter = mhz => { writes.Add(mhz); return true; };

            vm.ApplyCpuFrequencyCapForMode(4); // Performance: no cap, none set by PreySense → untouched
            CollectionAssert.AreEqual(Array.Empty<int>(), writes);

            vm.ApplyCpuFrequencyCapForMode(1); // Balanced: 4.0 GHz
            CollectionAssert.AreEqual(new[] { 4000 }, writes);
            Assert.AreEqual(4000, store.GetCurrent().Tuning.LastAppliedCpuFrequencyCapMhz);

            vm.ApplyCpuFrequencyCapForMode(5); // Turbo: no cap → remove ours
            CollectionAssert.AreEqual(new[] { 4000, 0 }, writes);
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedCpuFrequencyCapMhz);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void FrequencyCap_RejectsOutOfRangeValues()
    {
        Assert.IsFalse(PreySense.Mode.CpuFrequencyCap.IsValidCap(null));
        Assert.IsFalse(PreySense.Mode.CpuFrequencyCap.IsValidCap(0));
        Assert.IsFalse(PreySense.Mode.CpuFrequencyCap.IsValidCap(100));
        Assert.IsTrue(PreySense.Mode.CpuFrequencyCap.IsValidCap(4000));
    }

    [TestMethod]
    public void NewModeFields_RoundTripThroughConfigFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseExtras-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "preysense.json");
            new ConfigurationService(path).Update(config =>
            {
                config.Tuning.Eco.ProchotOffsetOverrideCelsius = 35;
                config.Tuning.Eco.CpuMaxFrequencyMhz = 2400;
            });
            var eco = new ConfigurationService(path).GetCurrent().Tuning.Eco;
            Assert.AreEqual(35, eco.ProchotOffsetOverrideCelsius);
            Assert.AreEqual(2400, eco.CpuMaxFrequencyMhz);
            Assert.AreEqual(35, eco.Clone().ProchotOffsetOverrideCelsius);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void Diagnostics_RestOfSystemIsBatteryMinusPackage()
    {
        var snapshot = new PowerDiagnosticsSnapshot(false, 50, 21.5, 0, 6.5, Array.Empty<PackageCStateResidency>(), false, 4, null);
        Assert.AreEqual(15.0, snapshot.RestOfSystemWatts!.Value, 0.001);
        Assert.AreEqual("D3 (asleep)", PowerDiagnosticsSampler.DescribeGpuState(4, null));
        Assert.AreEqual("Disabled", PowerDiagnosticsSampler.DescribeGpuState(null, true));
    }

    [TestMethod]
    public void Diagnostics_SamplerWithoutDriverReportsNoCStates()
    {
        var sampler = new PowerDiagnosticsSampler(null);
        Assert.IsFalse(sampler.PackageCStatesSupported);
    }

    [TestMethod]
    [DoNotParallelize] // GpuClockLock is process-wide state
    public void GpuClockLock_WaitsWhileAsleep_AndAppliesOnWake()
    {
        var oldState = PreySense.Gpu.GpuClockLock.PowerStateReader;
        var oldWriter = PreySense.Gpu.GpuClockLock.DriverWriter;
        var oldDisabled = PreySense.Gpu.GpuClockLock.DeviceDisabledReader;
        try
        {
            int? state = 4;
            var writes = new List<int>();
            PreySense.Gpu.GpuClockLock.PowerStateReader = () => state;
            PreySense.Gpu.GpuClockLock.DeviceDisabledReader = () => false;
            PreySense.Gpu.GpuClockLock.DriverWriter = mhz => { writes.Add(mhz); return 0; };

            Assert.IsFalse(PreySense.Gpu.GpuClockLock.Request(2000, false)); // asleep: no driver call
            Assert.IsEmpty(writes);
            Assert.IsTrue(PreySense.Gpu.GpuClockLock.IsPending);

            state = 1;
            Assert.IsTrue(PreySense.Gpu.GpuClockLock.Request(2000, false));
            CollectionAssert.AreEqual(new[] { 2000 }, writes);
            Assert.IsFalse(PreySense.Gpu.GpuClockLock.IsPending);

            PreySense.Gpu.GpuClockLock.MarkDriverRestarted(); // driver restarted while awake: sent again
            CollectionAssert.AreEqual(new[] { 2000, 2000 }, writes);

            Assert.IsTrue(PreySense.Gpu.GpuClockLock.Request(0, true)); // remove
            CollectionAssert.AreEqual(new[] { 2000, 2000, 0 }, writes);
            Assert.IsFalse(PreySense.Gpu.GpuClockLock.Request(0, false)); // nothing to do
            Assert.HasCount(3, writes);
        }
        finally
        {
            PreySense.Gpu.GpuClockLock.Stop();
            PreySense.Gpu.GpuClockLock.PowerStateReader = oldState;
            PreySense.Gpu.GpuClockLock.DriverWriter = oldWriter;
            PreySense.Gpu.GpuClockLock.DeviceDisabledReader = oldDisabled;
        }
    }

    [TestMethod]
    public void GpuClockLimit_PerMode_RemovesOnlyItsOwnLimit()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseGpuLock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config => config.Tuning.Quiet.GpuMaxClockMhz = 1800);
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var requests = new List<(int Mhz, bool Reset)>();
            vm.GpuClockLockRequester = (mhz, reset) => { requests.Add((mhz, reset)); return true; };

            vm.ApplyGpuClockLockForMode(1); // Balanced, nothing of ours in place
            Assert.AreEqual((0, false), requests[^1]);

            vm.ApplyGpuClockLockForMode(0); // Quiet: 1800 MHz
            Assert.AreEqual((1800, false), requests[^1]);
            Assert.AreEqual(1800, store.GetCurrent().Tuning.LastAppliedGpuClockLockMhz);

            vm.ApplyGpuClockLockForMode(5); // Turbo: remove ours
            Assert.AreEqual((0, true), requests[^1]);
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedGpuClockLockMhz);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void BackgroundSleep_FollowsWindowAndSetting()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseSleep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var calls = new List<bool>();
            vm.EfficiencyModeSetter = on => calls.Add(on);

            Assert.IsTrue(vm.IsSleepInBackgroundEnabled); // on by default
            vm.SetBackgroundSleep(true);
            vm.SetBackgroundSleep(true); // no repeat
            Assert.IsTrue(vm.IsBackgroundAsleep);
            vm.SetBackgroundSleep(false);
            CollectionAssert.AreEqual(new[] { true, false }, calls);

            store.Update(config => config.General.SleepInBackground = false);
            vm.SetBackgroundSleep(true); // setting off: stays awake
            Assert.IsFalse(vm.IsBackgroundAsleep);
            CollectionAssert.AreEqual(new[] { true, false }, calls);
        }
        finally { Directory.Delete(folder, true); }
    }
}
