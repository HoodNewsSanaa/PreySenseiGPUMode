using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PreySense.Gpu;

/// <summary>
/// iGPU Only fork: building blocks for the per-mode "RTX guard", which keeps things from waking the RTX while it
/// is meant to sleep. Nothing here talks to NVIDIA's driver: services are stopped through Windows, the apps on the
/// RTX are read from Windows' own GPU counters, and apps are moved with Windows' per-app graphics preference.
/// </summary>
public static class RtxGuard
{
    /// <summary>NVIDIA background services that poll the GPU (Display Container, NVIDIA app container, FrameView).</summary>
    public static readonly string[] PollingServices =
    {
        "NVDisplay.ContainerLocalSystem",
        "NvContainerLocalSystem",
        "FvSvc"
    };

    /// <summary>Stops the given services; returns the ones that were running (so they can be started again).</summary>
    public static List<string> StopServices(IEnumerable<string> names)
    {
        // Ask all of them to stop first, then wait (some, like the NVIDIA app container, take a while).
        var stopping = new List<string>();
        foreach (string name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) continue;
                sc.Stop();
                stopping.Add(name);
            }
            catch (InvalidOperationException) { /* not installed, or can't be stopped */ }
            catch (Exception ex) { AppLogger.Error($"[RTX guard] Could not stop {name}: {ex.Message}"); }
        }
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        foreach (string name in stopping)
        {
            try
            {
                using var sc = new ServiceController(name);
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero) sc.WaitForStatus(ServiceControllerStatus.Stopped, left);
                AppLogger.Log($"[RTX guard] Stopped {name}.");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                AppLogger.Log($"[RTX guard] {name} is still stopping.");
            }
            catch (Exception ex) { AppLogger.Error($"[RTX guard] {name}: {ex.Message}"); }
        }
        return stopping; // a slow stop still counts, so the service is started again later
    }

    /// <summary>True when any of the given services is running again (for example restarted by its recovery settings).</summary>
    public static bool AnyRunning(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>The service NVIDIA's display switching (Hybrid / Auto / Advanced Optimus) goes through.</summary>
    public const string DisplayContainerService = "NVDisplay.ContainerLocalSystem";

    /// <summary>Starts a service and waits until it runs. Returns true when it was stopped and is now running.</summary>
    public static bool StartAndWait(string name, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Running) return false;
            if (sc.Status is ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            AppLogger.Log($"[RTX guard] Started {name} for a GPU mode switch.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RTX guard] Could not start {name}: {ex.Message}");
            return false;
        }
    }

    public static void StartServices(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) continue;
                sc.Start();
                AppLogger.Log($"[RTX guard] Started {name} again.");
            }
            catch (Exception ex) { AppLogger.Error($"[RTX guard] Could not start {name}: {ex.Message}"); }
        }
    }

    // ---- Windows per-app graphics preference (Settings → System → Display → Graphics) ----

    private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string PowerSavingValue = "GpuPreference=1;";

    /// <summary>
    /// Sets an app to "Power saving" in Windows' graphics settings. Returns false (and changes nothing) when the app
    /// already has a choice of its own, so a choice the user made is never overwritten.
    /// </summary>
    public static bool SetPowerSaving(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesKey, writable: true);
            if (key.GetValue(exePath) is not null) return false;
            key.SetValue(exePath, PowerSavingValue, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX guard] Could not set the graphics preference: " + ex.Message);
            return false;
        }
    }

    /// <summary>Removes a preference PreySense added, unless it was changed since.</summary>
    public static void RemovePowerSaving(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKey, writable: true);
            if (key?.GetValue(exePath) is string value && value == PowerSavingValue)
                key.DeleteValue(exePath, throwOnMissingValue: false);
        }
        catch (Exception ex) { AppLogger.Error("[RTX guard] Could not remove the graphics preference: " + ex.Message); }
    }

    // ---- Which processes are on the RTX (from Windows' GPU performance counters) ----

    /// <summary>
    /// The LUIDs (as Windows' GPU counters spell them, "0xHIGH_0xLOW") of NVIDIA adapters, from the adapter list
    /// DirectX keeps in the registry.
    /// </summary>
    public static HashSet<string> NvidiaAdapterLuids()
    {
        var luids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var directX = hklm.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            if (directX is null) return luids;
            foreach (string sub in directX.GetSubKeyNames())
            {
                using var adapter = directX.OpenSubKey(sub);
                if (adapter?.GetValue("VendorId") is not int vendor || vendor != 0x10DE) continue;
                if (adapter.GetValue("AdapterLuid") is long luid)
                    luids.Add(FormatLuid(luid));
            }
        }
        catch (Exception ex) { AppLogger.Error("[RTX guard] " + ex.Message); }
        return luids;
    }

    public static string FormatLuid(long luid) =>
        string.Format(CultureInfo.InvariantCulture, "0x{0:X8}_0x{1:X8}", (uint)(luid >> 32), (uint)luid);

    /// <summary>
    /// Process IDs with graphics work or video memory on one of the given adapters. Empty when nothing is there;
    /// null when the counters can't be read.
    /// </summary>
    public static HashSet<int>? ProcessesOnAdapters(ISet<string> luids)
    {
        if (luids.Count == 0) return null;
        var pids = new HashSet<int>();
        try
        {
            Collect("SELECT Name, DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory",
                "DedicatedUsage", luids, pids);
            Collect("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine",
                "UtilizationPercentage", luids, pids);
            return pids;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RTX guard] GPU counters: " + ex.Message);
            return null;
        }
    }

    private static void Collect(string query, string property, ISet<string> luids, HashSet<int> pids)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", query);
        foreach (ManagementObject item in searcher.Get())
        {
            using (item)
            {
                if (item["Name"] is not string name || Convert.ToUInt64(item[property] ?? 0UL) == 0) continue;
                if (TryParseCounterInstance(name, out int pid, out string luid) && luids.Contains(luid))
                    pids.Add(pid);
            }
        }
    }

    /// <summary>Parses "pid_1234_luid_0x00000000_0x0000ABCD_phys_0..." into its process ID and LUID.</summary>
    public static bool TryParseCounterInstance(string name, out int pid, out string luid)
    {
        pid = 0;
        luid = string.Empty;
        const string pidPrefix = "pid_", luidPrefix = "_luid_";
        if (!name.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        int luidAt = name.IndexOf(luidPrefix, StringComparison.OrdinalIgnoreCase);
        if (luidAt < 0 || !int.TryParse(name.AsSpan(pidPrefix.Length, luidAt - pidPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            return false;
        int start = luidAt + luidPrefix.Length;
        int physAt = name.IndexOf("_phys", start, StringComparison.OrdinalIgnoreCase);
        luid = physAt > start ? name[start..physAt] : name[start..];
        return luid.Length > 0;
    }
}
