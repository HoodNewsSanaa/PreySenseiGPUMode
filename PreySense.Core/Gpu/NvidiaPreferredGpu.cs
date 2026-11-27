using System;
using System.Runtime.InteropServices;

namespace PreySense.Gpu;

/// <summary>Result of reading or writing the NVIDIA driver settings database.</summary>
public enum NvidiaSettingsResult
{
    Ok,
    /// <summary>No working NVIDIA GPU (driver missing, or the RTX switched off by iGPU Only): try again later.</summary>
    NoNvidiaGpu,
    Failed
}

/// <summary>Driver-default or explicit values of the two Optimus settings, as stored in the global profile.</summary>
public readonly record struct NvidiaPreferredGpuValues(uint? McCompat, uint? RenderingMode);

/// <summary>
/// iGPU Only fork: NVIDIA Control Panel's global "Preferred graphics processor", written to the driver's settings
/// database (DRS) through NVAPI, the same way NVIDIA Profile Inspector does. Apps with their own NVIDIA profile
/// or a Windows per-app graphics preference keep their own choice.
/// </summary>
public static class NvidiaPreferredGpu
{
    /// <summary>Values the Control Panel uses for the Optimus settings.</summary>
    public const uint Integrated = 0x00;
    public const uint Nvidia = 0x01;
    public const uint AutoSelect = 0x10;

    public static bool IsValid(int? value) => value is (int)Integrated or (int)Nvidia or (int)AutoSelect;

    private const uint ShimMcCompatId = 0x10F9DC80;
    private const uint ShimRenderingModeId = 0x10F9DC81;

    private const int NvapiOk = 0;
    private const int NvapiNvidiaDeviceNotFound = -6;
    private const int NvapiSettingNotFound = -160;

    // NVDRS_SETTING (version 1): 0x3020 bytes, all fields 4-byte aligned.
    private const int SettingSize = 0x3020;
    private const uint SettingVersion = SettingSize | (1u << 16);
    private const int OffsetSettingId = 0x1004;
    private const int OffsetSettingType = 0x1008;
    private const int OffsetIsCurrentPredefined = 0x1010;
    private const int OffsetCurrentValue = 0x201C;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Query(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSessionFn(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetGlobalProfileFn(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, [In, Out] byte[] setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSettingFn(IntPtr session, IntPtr profile, [In] byte[] setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);

    private static readonly object Gate = new();

    /// <summary>Reads the current values; null means the driver default is in use.</summary>
    public static NvidiaSettingsResult Read(out NvidiaPreferredGpuValues values)
    {
        NvidiaPreferredGpuValues read = default;
        var result = WithGlobalProfile((api, session, profile) =>
        {
            read = new(ReadValue(api, session, profile, ShimMcCompatId), ReadValue(api, session, profile, ShimRenderingModeId));
            return true;
        }, save: false);
        values = read;
        return result;
    }

    /// <summary>Sets the global preferred GPU (<see cref="Integrated"/>, <see cref="Nvidia"/> or <see cref="AutoSelect"/>).</summary>
    public static NvidiaSettingsResult Apply(uint value) =>
        WithGlobalProfile((api, session, profile) =>
            WriteValue(api, session, profile, ShimMcCompatId, value) &&
            WriteValue(api, session, profile, ShimRenderingModeId, value), save: true);

    /// <summary>Writes back values read earlier; a null value returns that setting to the driver default.</summary>
    public static NvidiaSettingsResult Restore(NvidiaPreferredGpuValues values) =>
        WithGlobalProfile((api, session, profile) =>
            RestoreValue(api, session, profile, ShimMcCompatId, values.McCompat) &&
            RestoreValue(api, session, profile, ShimRenderingModeId, values.RenderingMode), save: true);

    private sealed class Api
    {
        public required CreateSessionFn CreateSession;
        public required SessionFn DestroySession, LoadSettings, SaveSettings;
        public required GetGlobalProfileFn GetGlobalProfile;
        public required GetSettingFn GetSetting;
        public required SetSettingFn SetSetting;
        public required DeleteSettingFn DeleteSetting;
    }

    private static Api? _api;

    private static NvidiaSettingsResult WithGlobalProfile(Func<Api, IntPtr, IntPtr, bool> action, bool save)
    {
        // Never at the same time as a GPU mode switch or an RTX restart.
        using var nvidiaLease = PreySense.Core.Services.HardwareOperationGate.TryEnterNvidiaWork(TimeSpan.FromSeconds(30));
        if (nvidiaLease is null)
        {
            AppLogger.Error("[Preferred GPU] The GPU is busy switching; NVIDIA's settings were left for later.");
            return NvidiaSettingsResult.Failed;
        }
        lock (Gate)
        {
            IntPtr session = IntPtr.Zero;
            Api? api = null;
            try
            {
                var status = Load(out api);
                if (status != NvidiaSettingsResult.Ok) return status;
                if (api!.CreateSession(out session) != NvapiOk) return NvidiaSettingsResult.Failed;
                if (api.LoadSettings(session) != NvapiOk ||
                    api.GetGlobalProfile(session, out IntPtr profile) != NvapiOk ||
                    !action(api, session, profile))
                    return NvidiaSettingsResult.Failed;
                if (save && api.SaveSettings(session) != NvapiOk) return NvidiaSettingsResult.Failed;
                return NvidiaSettingsResult.Ok;
            }
            catch (DllNotFoundException)
            {
                return NvidiaSettingsResult.NoNvidiaGpu;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[Preferred GPU] NVAPI: " + ex.Message);
                return NvidiaSettingsResult.Failed;
            }
            finally
            {
                if (session != IntPtr.Zero) try { api?.DestroySession(session); } catch { }
            }
        }
    }

    private static NvidiaSettingsResult Load(out Api? api)
    {
        api = _api;
        IntPtr initialize = Query(0x0150E828);
        if (initialize == IntPtr.Zero) return NvidiaSettingsResult.NoNvidiaGpu;
        int init = Marshal.GetDelegateForFunctionPointer<InitializeFn>(initialize)();
        if (init == NvapiNvidiaDeviceNotFound) return NvidiaSettingsResult.NoNvidiaGpu;
        if (init != NvapiOk) return NvidiaSettingsResult.Failed;
        if (api != null) return NvidiaSettingsResult.Ok;

        T Get<T>(uint id) where T : Delegate
        {
            IntPtr p = Query(id);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"NVAPI 0x{id:X8}");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        api = _api = new Api
        {
            CreateSession = Get<CreateSessionFn>(0x0694D52E),
            DestroySession = Get<SessionFn>(0xDAD9CFF8),
            LoadSettings = Get<SessionFn>(0x375DBD6B),
            SaveSettings = Get<SessionFn>(0xFCBC7E14),
            GetGlobalProfile = Get<GetGlobalProfileFn>(0x617BFF9F),
            GetSetting = Get<GetSettingFn>(0x73BF8338),
            SetSetting = Get<SetSettingFn>(0x577DD202),
            DeleteSetting = Get<DeleteSettingFn>(0xE4A26362),
        };
        return NvidiaSettingsResult.Ok;
    }

    private static byte[] NewSetting(uint settingId)
    {
        var buffer = new byte[SettingSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), SettingVersion);
        BitConverter.TryWriteBytes(buffer.AsSpan(OffsetSettingId), settingId);
        return buffer;
    }

    /// <summary>The value set in the global profile, or null when the driver default is in use.</summary>
    private static uint? ReadValue(Api api, IntPtr session, IntPtr profile, uint settingId)
    {
        var buffer = NewSetting(settingId);
        int status = api.GetSetting(session, profile, settingId, buffer);
        if (status == NvapiSettingNotFound) return null;
        if (status != NvapiOk) throw new InvalidOperationException($"NvAPI_DRS_GetSetting 0x{settingId:X8} returned {status}");
        if (BitConverter.ToUInt32(buffer, OffsetIsCurrentPredefined) != 0) return null;
        return BitConverter.ToUInt32(buffer, OffsetCurrentValue);
    }

    private static bool WriteValue(Api api, IntPtr session, IntPtr profile, uint settingId, uint value)
    {
        var buffer = NewSetting(settingId);
        BitConverter.TryWriteBytes(buffer.AsSpan(OffsetSettingType), 0u); // NVDRS_DWORD_TYPE
        BitConverter.TryWriteBytes(buffer.AsSpan(OffsetCurrentValue), value);
        int status = api.SetSetting(session, profile, buffer);
        if (status != NvapiOk) AppLogger.Error($"[Preferred GPU] NvAPI_DRS_SetSetting 0x{settingId:X8} returned {status}");
        return status == NvapiOk;
    }

    private static bool RestoreValue(Api api, IntPtr session, IntPtr profile, uint settingId, uint? value)
    {
        if (value is uint v) return WriteValue(api, session, profile, settingId, v);
        int status = api.DeleteSetting(session, profile, settingId);
        return status is NvapiOk or NvapiSettingNotFound;
    }
}
