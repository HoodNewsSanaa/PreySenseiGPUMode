using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PreySense.Gpu;

/// <summary>NVIDIA's PhysX processor choice as stored in the registry; null values are absent.</summary>
public readonly record struct NvidiaPhysxValues(int? Auto, uint? GpuId);

/// <summary>
/// iGPU Only fork: NVIDIA Control Panel's "Set PhysX configuration → PhysX processor". The Control Panel keeps it
/// as two values under the NVIDIA driver's service key: NvCplPhysxAuto (1 = Auto-select, 0 = chosen by hand) and
/// physxGpuId (4 bytes: the GPU's id, or all zeros for the CPU). PhysX reads them when a PhysX app starts, so
/// changing them never wakes the RTX.
/// </summary>
public static class NvidiaPhysx
{
    /// <summary>The per-mode value meaning "run PhysX on the CPU".</summary>
    public const int Cpu = 0;

    public static bool IsValid(int? value) => value is Cpu;

    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\nvlddmkm";
    private const string AutoValue = "NvCplPhysxAuto";
    private const string GpuIdValue = "physxGpuId";

    /// <summary>Reads the current values. Returns false when NVIDIA's PhysX values can't be found.</summary>
    public static bool Read(out NvidiaPhysxValues values)
    {
        values = default;
        using var key = OpenPhysxKey(writable: false);
        if (key is null) return false;
        values = new NvidiaPhysxValues(ReadAuto(key), ReadGpuId(key));
        return true;
    }

    /// <summary>Sets PhysX to run on the CPU (what the Control Panel writes for "CPU").</summary>
    public static bool ApplyCpu() => Write(new NvidiaPhysxValues(0, 0));

    /// <summary>Writes back values read earlier; a null value is removed.</summary>
    public static bool Restore(NvidiaPhysxValues values) => Write(values);

    private static bool Write(NvidiaPhysxValues values)
    {
        try
        {
            using var key = OpenPhysxKey(writable: true);
            if (key is null) return false;
            if (values.Auto is int auto) key.SetValue(AutoValue, auto, RegistryValueKind.DWord);
            else key.DeleteValue(AutoValue, throwOnMissingValue: false);
            if (values.GpuId is uint id) key.SetValue(GpuIdValue, BitConverter.GetBytes(id), RegistryValueKind.Binary);
            else key.DeleteValue(GpuIdValue, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PhysX] " + ex.Message);
            return false;
        }
    }

    private static int? ReadAuto(RegistryKey key) => key.GetValue(AutoValue) switch
    {
        int i => i,
        _ => null
    };

    private static uint? ReadGpuId(RegistryKey key) => key.GetValue(GpuIdValue) switch
    {
        byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
        int i => unchecked((uint)i),
        _ => null
    };

    /// <summary>
    /// The NVIDIA service key, or the subkey under it, that holds the PhysX values (searched a few levels deep,
    /// because the location differs between driver versions).
    /// </summary>
    private static RegistryKey? OpenPhysxKey(bool writable)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            string? path = FindPhysxKeyPath(hklm);
            return path is null ? null : hklm.OpenSubKey(path, writable);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PhysX] " + ex.Message);
            return null;
        }
    }

    private static string? FindPhysxKeyPath(RegistryKey hklm)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((ServiceKey, 0));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            using var key = hklm.OpenSubKey(path, writable: false);
            if (key is null) continue;
            foreach (string name in key.GetValueNames())
                if (name.Equals(GpuIdValue, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(AutoValue, StringComparison.OrdinalIgnoreCase))
                    return path;
            if (depth >= 3) continue;
            foreach (string sub in key.GetSubKeyNames())
                queue.Enqueue((path + "\\" + sub, depth + 1));
        }
        return null;
    }
}
