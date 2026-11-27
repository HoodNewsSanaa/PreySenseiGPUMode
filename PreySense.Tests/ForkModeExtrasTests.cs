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

    [TestMethod]
    public void PcieLinkPower_SavesWindowsValuesAndRestoresThem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySensePcie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config =>
            {
                config.Tuning.Eco.PcieLinkPowerSaving = 2;
                config.Tuning.Balanced.PcieLinkPowerSaving = 1;
            });
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var writes = new List<int>();
            var restores = new List<(int? Ac, int? Dc)>();
            vm.PcieLinkPowerReader = dc => dc ? 1 : 0; // Windows had AC Off, DC Moderate
            vm.PcieLinkPowerWriter = level => { writes.Add(level); return true; };
            vm.PcieLinkPowerRestorer = (ac, dc) => { restores.Add((ac, dc)); return true; };

            vm.ApplyPcieLinkPowerSavingForMode(4); // Performance: leave alone, nothing of ours
            Assert.IsEmpty(writes);
            Assert.IsEmpty(restores);

            vm.ApplyPcieLinkPowerSavingForMode(6); // Eco: maximum
            vm.PcieLinkPowerReader = dc => 2;      // now reads PreySense's own value; must not be saved
            vm.ApplyPcieLinkPowerSavingForMode(1); // Balanced: moderate
            CollectionAssert.AreEqual(new[] { 2, 1 }, writes);
            Assert.AreEqual(0, store.GetCurrent().Tuning.OriginalPcieLinkPowerSavingAc);
            Assert.AreEqual(1, store.GetCurrent().Tuning.OriginalPcieLinkPowerSavingDc);

            vm.ApplyPcieLinkPowerSavingForMode(5); // Turbo: back to Windows' values
            Assert.AreEqual((0, 1), restores.Single());
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedPcieLinkPowerSaving);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void CpuMaxState_KeepsPAndECoresSeparateAndRestoresWindowsValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseCpuState-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config =>
            {
                config.Tuning.Eco.CpuMaxStatePercent = 99;
                config.Tuning.Eco.CpuMaxStateEfficientPercent = 70;
                config.Tuning.Balanced.CpuMaxStatePercent = 90; // E-cores left to Windows
            });
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var p = PreySense.Mode.CpuMaxProcessorState.PerformanceCores;
            var e = PreySense.Mode.CpuMaxProcessorState.EfficientCores;
            var writes = new List<(string Name, int Percent)>();
            var restores = new List<(string Name, int? Ac, int? Dc)>();
            vm.CpuMaxStateReader = (setting, dc) => setting == p ? (dc ? 80 : 100) : 100;
            vm.CpuMaxStateWriter = (setting, percent) => { writes.Add((setting.Name, percent)); return true; };
            vm.CpuMaxStateRestorer = (setting, ac, dc) => { restores.Add((setting.Name, ac, dc)); return true; };

            vm.ApplyCpuMaxStateForMode(4); // Performance: nothing set, nothing of ours
            Assert.IsEmpty(writes);
            Assert.IsEmpty(restores);

            vm.ApplyCpuMaxStateForMode(6); // Eco: P 99, E 70
            vm.CpuMaxStateReader = (_, _) => 55; // now reads PreySense's own values; must not be saved
            vm.ApplyCpuMaxStateForMode(1); // Balanced: P 90, E back to Windows
            CollectionAssert.AreEqual(new[] { (p.Name, 99), (e.Name, 70), (p.Name, 90) }, writes);
            Assert.AreEqual((e.Name, (int?)100, (int?)100), restores.Single());
            var tuning = store.GetCurrent().Tuning;
            Assert.AreEqual(100, tuning.OriginalCpuMaxStatePercentAc);
            Assert.AreEqual(80, tuning.OriginalCpuMaxStatePercentDc);
            Assert.AreEqual(90, tuning.LastAppliedCpuMaxStatePercent);
            Assert.IsNull(tuning.LastAppliedCpuMaxStateEfficientPercent);

            vm.ApplyCpuMaxStateForMode(5); // Turbo: P back to Windows' values
            Assert.AreEqual((p.Name, (int?)100, (int?)80), restores.Last());
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedCpuMaxStatePercent);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void PreferredGpu_WaitsForTheRtxSkipsRepeatsAndRestoresNvidiaValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySensePreferredGpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config =>
            {
                config.Tuning.Eco.PreferredGpu = (int)PreySense.Gpu.NvidiaPreferredGpu.Integrated;
                config.Tuning.Quiet.PreferredGpu = (int)PreySense.Gpu.NvidiaPreferredGpu.Integrated;
            });
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var original = new PreySense.Gpu.NvidiaPreferredGpuValues(null, 0x10); // McCompat at driver default
            var result = PreySense.Gpu.NvidiaSettingsResult.NoNvidiaGpu;
            var writes = new List<uint>();
            var restores = new List<PreySense.Gpu.NvidiaPreferredGpuValues>();
            vm.PreferredGpuReader = () => (result, original);
            vm.PreferredGpuWriter = value => { writes.Add(value); return PreySense.Gpu.NvidiaSettingsResult.Ok; };
            vm.PreferredGpuRestorer = values => { restores.Add(values); return PreySense.Gpu.NvidiaSettingsResult.Ok; };

            vm.ApplyPreferredGpuForMode(6); // Eco while the RTX is off: nothing written, nothing remembered
            Assert.IsEmpty(writes);
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedPreferredGpu);

            result = PreySense.Gpu.NvidiaSettingsResult.Ok; // RTX back on
            vm.ApplyPreferredGpuForMode(6);
            vm.ApplyPreferredGpuForMode(0); // Quiet: same choice, no driver call
            CollectionAssert.AreEqual(new[] { PreySense.Gpu.NvidiaPreferredGpu.Integrated }, writes);
            Assert.IsNull(store.GetCurrent().Tuning.OriginalPreferredGpuMcCompat);
            Assert.AreEqual(0x10L, store.GetCurrent().Tuning.OriginalPreferredGpuRenderingMode);

            vm.ApplyPreferredGpuForMode(4); // Performance: left alone, so NVIDIA's values go back
            Assert.AreEqual(original, restores.Single());
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedPreferredGpu);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void Physx_SetsCpuAndRestoresNvidiaValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySensePhysx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config => config.Tuning.Eco.PhysxProcessor = PreySense.Gpu.NvidiaPhysx.Cpu);
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var original = new PreySense.Gpu.NvidiaPhysxValues(1, 0x200); // Auto-select on the RTX
            int cpuWrites = 0;
            var restores = new List<PreySense.Gpu.NvidiaPhysxValues>();
            vm.PhysxReader = () => original;
            vm.PhysxCpuWriter = () => { cpuWrites++; return true; };
            vm.PhysxRestorer = values => { restores.Add(values); return true; };

            vm.ApplyPhysxForMode(4); // Performance: nothing of ours
            Assert.AreEqual(0, cpuWrites);
            vm.ApplyPhysxForMode(6); // Eco: CPU
            vm.PhysxReader = () => new PreySense.Gpu.NvidiaPhysxValues(0, 0); // PreySense's own value; not saved
            vm.ApplyPhysxForMode(6);
            Assert.AreEqual(2, cpuWrites);
            Assert.AreEqual(1, store.GetCurrent().Tuning.OriginalPhysxAuto);
            Assert.AreEqual(0x200L, store.GetCurrent().Tuning.OriginalPhysxGpuId);

            vm.ApplyPhysxForMode(4); // back to NVIDIA's values
            Assert.AreEqual(original, restores.Single());
            Assert.IsNull(store.GetCurrent().Tuning.LastAppliedPhysxProcessor);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void ProcessorPolicy_WritesBothCoreTypesAndRestoresWindowsValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySensePolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config =>
            {
                config.Tuning.Eco.EppPercent = 80;
                config.Tuning.Eco.CoreSchedulingPolicy = PreySense.Mode.ProcessorPolicySettings.SchedulingEfficientOnly;
                config.Tuning.Eco.KeyboardLightingOff = true;
            });
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var writes = new List<(string Name, int Value)>();
            var restores = new List<(string Name, int? Ac, int? Dc)>();
            vm.PlanSettingReader = (setting, dc) => dc ? 50 : 33;
            vm.PlanSettingWriter = (setting, value) => { writes.Add((setting.Name, value)); return true; };
            vm.PlanSettingRestorer = (setting, ac, dc) => { restores.Add((setting.Name, ac, dc)); return true; };

            vm.ApplyProcessorPolicyForMode(6); // Eco
            Assert.HasCount(4, writes);
            CollectionAssert.AreEquivalent(new[] { 80, 80, 3, 3 }, writes.Select(w => w.Value).ToArray());
            Assert.HasCount(4, store.GetCurrent().Tuning.HeldPlanSettings);

            vm.ApplyProcessorPolicyForMode(6); // same values: nothing new written
            Assert.HasCount(4, writes);

            vm.ApplyProcessorPolicyForMode(4); // Performance: Windows' values back
            Assert.HasCount(4, restores);
            Assert.IsTrue(restores.All(r => r.Ac == 33 && r.Dc == 50));
            Assert.IsEmpty(store.GetCurrent().Tuning.HeldPlanSettings);

            vm.ApplyKeyboardLightingForMode(6);
            Assert.IsTrue(vm.IsModeLightingOff);
            vm.ApplyKeyboardLightingForMode(4);
            Assert.IsFalse(vm.IsModeLightingOff);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void RtxGuard_StopsServicesInGuardedModeAndStartsThemAfter()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PreySenseRtxGuard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConfigurationService(Path.Combine(folder, "preysense.json"));
            store.Update(config => config.Tuning.Eco.RtxGuard = true);
            var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            vm.TuningConfigurationStore = store;
            var started = new List<string>();
            vm.RtxGuardServiceStopper = names => new List<string> { "NVDisplay.ContainerLocalSystem" };
            vm.RtxGuardServiceStarter = names => started.AddRange(names);

            vm.ApplyRtxGuardForMode(4); // not guarded, nothing stopped: nothing to do
            Assert.IsFalse(vm.IsRtxGuardActive);
            Assert.IsEmpty(started);

            vm.ApplyRtxGuardForMode(6); // Eco: guard on
            Assert.IsTrue(vm.IsRtxGuardActive);
            CollectionAssert.AreEqual(new[] { "NVDisplay.ContainerLocalSystem" }, store.GetCurrent().Tuning.RtxGuardStoppedServices);

            vm.ApplyRtxGuardForMode(4); // guard off: services back
            Assert.IsFalse(vm.IsRtxGuardActive);
            CollectionAssert.AreEqual(new[] { "NVDisplay.ContainerLocalSystem" }, started);
            Assert.IsEmpty(store.GetCurrent().Tuning.RtxGuardStoppedServices);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void RtxGuard_ParsesCountersAndPicksOnlyUserApps()
    {
        Assert.IsTrue(PreySense.Gpu.RtxGuard.TryParseCounterInstance(
            "pid_1234_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D", out int pid, out string luid));
        Assert.AreEqual(1234, pid);
        Assert.AreEqual("0x00000000_0x0000D1F2", luid);
        Assert.IsTrue(PreySense.Gpu.RtxGuard.TryParseCounterInstance("pid_8_luid_0x00000001_0x00000002_phys_0", out _, out luid));
        Assert.AreEqual("0x00000001_0x00000002", luid);
        Assert.IsFalse(PreySense.Gpu.RtxGuard.TryParseCounterInstance("luid_0x0_0x1_phys_0", out _, out _));
        Assert.AreEqual("0x00000001_0x0000D1F2", PreySense.Gpu.RtxGuard.FormatLuid((1L << 32) | 0xD1F2));

        const string win = @"C:\Windows\";
        Assert.IsTrue(MainViewModel.IsMovableApp(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", win));
        Assert.IsFalse(MainViewModel.IsMovableApp(@"C:\Windows\System32\dwm.exe", win));
        Assert.IsFalse(MainViewModel.IsMovableApp(@"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA Overlay.exe", win));
        Assert.IsFalse(MainViewModel.IsMovableApp(@"C:\Users\x\Desktop\PreySense\PreySense.exe", win));
        Assert.IsFalse(MainViewModel.IsMovableApp(null, win));
    }

    [TestMethod]
    public void Updater_ParsesForkTagsAndComparesBuilds()
    {
        Assert.AreEqual((new Version(3, 1, 3), 7), PreySense.Frontend.Services.ForkUpdateService.ParseTag("igpu-3.1.3-7"));
        Assert.IsNull(PreySense.Frontend.Services.ForkUpdateService.ParseTag("v3.1.4"));
        Assert.IsNull(PreySense.Frontend.Services.ForkUpdateService.ParseTag("igpu-3.1.3"));

        var release = new PreySense.Frontend.Services.ForkRelease("igpu-3.1.3-9", new Version(3, 1, 3), 9,
            "https://github.com/x", "https://github.com/x/PreySense.exe", new string('a', 64), 0);
        Assert.IsTrue(PreySense.Frontend.Services.ForkUpdateService.IsNewer(release, new Version(3, 1, 3), 8));
        Assert.IsFalse(PreySense.Frontend.Services.ForkUpdateService.IsNewer(release, new Version(3, 1, 3), 9));
        Assert.IsFalse(PreySense.Frontend.Services.ForkUpdateService.IsNewer(release, new Version(3, 1, 4), 1));
        Assert.IsTrue(PreySense.Frontend.Services.ForkUpdateService.IsNewer(release, new Version(3, 1, 2), 50));
    }

    [TestMethod]
    public void Updater_RequiresGitHubAssetWithSha256Digest()
    {
        string sha = new string('b', 64);
        string good = $$"""
            {"tag_name":"igpu-3.1.4-12","html_url":"https://github.com/HoodNewsSanaa/PreySenseiGPUMode/releases/tag/igpu-3.1.4-12",
             "assets":[{"name":"PreySense.exe","size":2885124,
               "browser_download_url":"https://github.com/HoodNewsSanaa/PreySenseiGPUMode/releases/download/igpu-3.1.4-12/PreySense.exe",
               "digest":"sha256:{{sha}}"}]}
            """;
        var release = PreySense.Frontend.Services.ForkUpdateService.ParseRelease(good);
        Assert.IsNotNull(release);
        Assert.AreEqual(12, release.Build);
        Assert.AreEqual(sha, release.Sha256);

        Assert.IsNull(PreySense.Frontend.Services.ForkUpdateService.ParseRelease(good.Replace("sha256:" + sha, "md5:abc")));
        Assert.IsNull(PreySense.Frontend.Services.ForkUpdateService.ParseRelease(good.Replace("https://github.com/HoodNewsSanaa/PreySenseiGPUMode/releases/download", "http://evil.example")));
        Assert.IsNull(PreySense.Frontend.Services.ForkUpdateService.ParseRelease(good.Replace("igpu-3.1.4-12\",\"html", "v3.1.4\",\"html")));
    }
}
