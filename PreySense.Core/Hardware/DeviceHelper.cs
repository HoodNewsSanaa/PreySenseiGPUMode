using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using PreySense;

namespace PreySense.Helpers
{
    public static class DeviceHelper
    {
        private static readonly object PowerStateCacheGate = new();
        private static string? _cachedPowerStateHwId;
        private static int? _cachedPowerState;
        private static long _cachedPowerStateTicks;
        private static readonly long PowerStateCacheTicks = Stopwatch.Frequency; // ~1s

        private const uint DIF_PROPERTYCHANGE = 0x00000012;
        private const uint DICS_PROPCHANGE = 0x00000003;
        private const uint DICS_FLAG_GLOBAL = 0x00000001;
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DICS_ENABLE = 0x00000001;
        private const uint DICS_DISABLE = 0x00000002;
        private const uint DN_HAS_PROBLEM = 0x00000400;
        private const uint CM_PROB_DISABLED = 0x00000016;
        private const int CR_SUCCESS = 0;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

        private static readonly Guid DisplayClassGuid = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid classGuid;
            public int devInst;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public int cbSize;
            public int installFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER classInstallHeader;
            public int stateChange;
            public int scope;
            public int hwProfile;
        }

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, int devInst, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, int classInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property, out int propertyRegDataType, StringBuilder propertyBuffer, int propertyBufferSize, out int requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
        private static extern bool ReadDeviceProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            int property, out int dataType, byte[] buffer, int bufferSize, out int requiredSize);

        /// <summary>Reads Windows-maintained CM_POWER_DATA; does not query the GPU driver.</summary>
        public static int? GetDisplayDevicePowerState(string hardwareIdSubstring)
        {
            long now = Stopwatch.GetTimestamp();
            lock (PowerStateCacheGate)
            {
                if (string.Equals(_cachedPowerStateHwId, hardwareIdSubstring, StringComparison.OrdinalIgnoreCase)
                    && now - _cachedPowerStateTicks < PowerStateCacheTicks)
                    return _cachedPowerState;
            }

            int? state = QueryDisplayDevicePowerState(hardwareIdSubstring);
            lock (PowerStateCacheGate)
            {
                _cachedPowerStateHwId = hardwareIdSubstring;
                _cachedPowerState = state;
                _cachedPowerStateTicks = now;
            }
            return state;
        }

        public static void InvalidateDisplayDevicePowerStateCache()
        {
            lock (PowerStateCacheGate)
            {
                _cachedPowerStateHwId = null;
                _cachedPowerState = null;
                _cachedPowerStateTicks = 0;
            }
        }

        private static int? QueryDisplayDevicePowerState(string hardwareIdSubstring)
        {
            Guid displayGuid = DisplayClassGuid;
            IntPtr devices = SetupDiGetClassDevs(ref displayGuid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devices == IntPtr.Zero || devices == new IntPtr(-1)) return null;
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var ids = new byte[4096];
                for (int index = 0; SetupDiEnumDeviceInfo(devices, index, ref info); index++)
                {
                    if (!ReadDeviceProperty(devices, ref info, 1, out _, ids, ids.Length, out int size)) continue;
                    if (!Encoding.Unicode.GetString(ids, 0, size).Contains(hardwareIdSubstring, StringComparison.OrdinalIgnoreCase)) continue;
                    var power = new byte[256];
                    return ReadDeviceProperty(devices, ref info, 0x1E, out _, power, power.Length, out size)
                        ? DecodeDisplayDevicePowerState(power.AsSpan(0, size)) : null;
                }
                return null;
            }
            finally { SetupDiDestroyDeviceInfoList(devices); }
        }

        public static int? DecodeDisplayDevicePowerState(ReadOnlySpan<byte> powerData)
        {
            if (powerData.Length < 8) return null;
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(powerData);
            if (size < 8 || size > powerData.Length) return null;
            int state = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(powerData.Slice(4));
            return state is >= 1 and <= 4 ? state : null;
        }

        /// <summary>
        /// Disables or Enables any display device matching the hardware ID substring (e.g. "VEN_10DE" for NVIDIA dGPU).
        /// </summary>

        /// <summary>
        /// Restarts / power-cycles a display device node matching the hardware ID substring (e.g. "VEN_10DE")
        /// without requiring a full system reboot, matching Acer hardware service behavior.
        /// </summary>
        public static bool RestartDevice(string hardwareIdSubstring)
        {
            Guid displayGuid = DisplayClassGuid;
            IntPtr devInfoSet = SetupDiGetClassDevs(ref displayGuid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
                return false;

            bool success = false;
            try
            {
                SP_DEVINFO_DATA devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf(devInfoData);

                int memberIndex = 0;
                while (SetupDiEnumDeviceInfo(devInfoSet, memberIndex, ref devInfoData))
                {
                    memberIndex++;

                    StringBuilder buffer = new StringBuilder(1000);
                    if (SetupDiGetDeviceRegistryProperty(devInfoSet, ref devInfoData, 1, out _, buffer, buffer.Capacity, out _))
                    {
                        string hwId = buffer.ToString().ToUpper();
                        if (hwId.Contains(hardwareIdSubstring.ToUpper()))
                        {
                            // First attempt: DICS_PROPCHANGE (PnP driver restart without disabling DevNode)
                            SP_PROPCHANGE_PARAMS pcp = new SP_PROPCHANGE_PARAMS();
                            pcp.classInstallHeader.cbSize = Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
                            pcp.classInstallHeader.installFunction = (int)DIF_PROPERTYCHANGE;
                            pcp.stateChange = (int)DICS_PROPCHANGE;
                            pcp.scope = (int)DICS_FLAG_GLOBAL;
                            pcp.hwProfile = 0;

                            if (SetupDiSetClassInstallParams(devInfoSet, ref devInfoData, ref pcp, Marshal.SizeOf(pcp)))
                            {
                                if (SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref devInfoData))
                                {
                                    success = true;
                                    AppLogger.Log($"PnP Device Restarted (DICS_PROPCHANGE) successfully: {hwId}");
                                }
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"RestartDevice failed: {ex.Message}");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            if (success)
                InvalidateDisplayDevicePowerStateCache();
            return success;
        }

        private static bool TryFindDisplayDevice(string hardwareIdSubstring, out IntPtr devInfoSet, out SP_DEVINFO_DATA devInfoData)
        {
            devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = Marshal.SizeOf(devInfoData);
            Guid displayGuid = DisplayClassGuid;
            // No DIGCF_PRESENT: a disabled adapter must be found too. Stale (non-present) entries are skipped
            // below because CM_Get_DevNode_Status fails for them.
            devInfoSet = SetupDiGetClassDevs(ref displayGuid, null, IntPtr.Zero, 0);
            if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
            {
                devInfoSet = IntPtr.Zero;
                return false;
            }

            string wanted = hardwareIdSubstring.ToUpperInvariant();
            int memberIndex = 0;
            while (SetupDiEnumDeviceInfo(devInfoSet, memberIndex, ref devInfoData))
            {
                memberIndex++;
                StringBuilder buffer = new StringBuilder(1000);
                if (SetupDiGetDeviceRegistryProperty(devInfoSet, ref devInfoData, 1, out _, buffer, buffer.Capacity, out _)
                    && buffer.ToString().ToUpperInvariant().Contains(wanted)
                    && CM_Get_DevNode_Status(out _, out _, devInfoData.devInst, 0) == CR_SUCCESS)
                    return true;
            }
            SetupDiDestroyDeviceInfoList(devInfoSet);
            devInfoSet = IntPtr.Zero;
            return false;
        }

        /// <summary>True when the matching display adapter is disabled in Windows, false when enabled,
        /// null when no such adapter exists or its state cannot be read.</summary>
        public static bool? IsDisplayDeviceDisabled(string hardwareIdSubstring)
        {
            try
            {
                if (!TryFindDisplayDevice(hardwareIdSubstring, out IntPtr set, out SP_DEVINFO_DATA data))
                    return null;
                try
                {
                    if (CM_Get_DevNode_Status(out uint status, out uint problem, data.devInst, 0) != CR_SUCCESS)
                        return null;
                    return (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"IsDisplayDeviceDisabled failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Enables or disables the matching display adapter (same as Device Manager).</summary>
        public static bool SetDisplayDeviceEnabled(string hardwareIdSubstring, bool enable)
        {
            IntPtr set = IntPtr.Zero;
            try
            {
                if (!TryFindDisplayDevice(hardwareIdSubstring, out set, out SP_DEVINFO_DATA data))
                {
                    AppLogger.Log($"SetDisplayDeviceEnabled: no display device matching {hardwareIdSubstring}");
                    return false;
                }
                SP_PROPCHANGE_PARAMS pcp = new SP_PROPCHANGE_PARAMS();
                pcp.classInstallHeader.cbSize = Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
                pcp.classInstallHeader.installFunction = (int)DIF_PROPERTYCHANGE;
                pcp.stateChange = (int)(enable ? DICS_ENABLE : DICS_DISABLE);
                pcp.scope = (int)DICS_FLAG_GLOBAL;
                pcp.hwProfile = 0;
                if (!SetupDiSetClassInstallParams(set, ref data, ref pcp, Marshal.SizeOf(pcp))
                    || !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data))
                {
                    AppLogger.Log($"SetDisplayDeviceEnabled({enable}) failed: Win32 error {Marshal.GetLastWin32Error()}");
                    return false;
                }
                AppLogger.Log($"Display device {hardwareIdSubstring} {(enable ? "enabled" : "disabled")}.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"SetDisplayDeviceEnabled failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (set != IntPtr.Zero && set.ToInt64() != -1) SetupDiDestroyDeviceInfoList(set);
                InvalidateDisplayDevicePowerStateCache();
            }
        }

        // ── iGPU Only fork: the RTX's HDMI/DisplayPort audio function ──
        // The RTX is one PCI device with two functions: the GPU and an "NVIDIA High Definition Audio" controller.
        // They share one power switch, so whenever Windows' audio system touches the audio function (listing
        // audio outputs, an app starting playback) the whole RTX powers up. Disabling the audio function stops
        // that; only audio over a monitor wired to the RTX is lost.

        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const int SPDRP_HARDWAREID = 0x00000001;
        private const int SPDRP_SERVICE = 0x00000004;

        private static string ReadMultiString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
        {
            var buffer = new byte[2048];
            if (!ReadDeviceProperty(set, ref data, property, out _, buffer, buffer.Length, out int size) || size <= 0)
                return string.Empty;
            return Encoding.Unicode.GetString(buffer, 0, Math.Min(size, buffer.Length)).Replace('\0', ' ');
        }

        /// <summary>Runs <paramref name="action"/> on the RTX's audio controller(s). Returns false when none is found.</summary>
        private static bool ForEachNvidiaAudioController(Func<IntPtr, SP_DEVINFO_DATA, bool> action)
        {
            Guid any = Guid.Empty;
            IntPtr set = SetupDiGetClassDevs(ref any, "PCI", IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return false;
            bool found = false;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    string ids = ReadMultiString(set, ref data, SPDRP_HARDWAREID);
                    if (ids.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string service = ReadMultiString(set, ref data, SPDRP_SERVICE).Trim();
                    if (!service.Equals("HDAudBus", StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if (!action(set, data)) return false;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        /// <summary>True when the RTX's audio controller is disabled, false when enabled, null when there is none.</summary>
        public static bool? IsNvidiaAudioDisabled()
        {
            bool? disabled = null;
            try
            {
                ForEachNvidiaAudioController((_, data) =>
                {
                    if (CM_Get_DevNode_Status(out uint status, out uint problem, data.devInst, 0) != CR_SUCCESS)
                        return true;
                    bool off = (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
                    disabled = (disabled ?? true) && off;   // "disabled" only when every controller is off
                    return true;
                });
            }
            catch (Exception ex) { AppLogger.Log($"IsNvidiaAudioDisabled failed: {ex.Message}"); }
            return disabled;
        }

        /// <summary>Enables or disables the RTX's audio controller. Returns false when it isn't there or Windows refused.</summary>
        public static bool SetNvidiaAudioEnabled(bool enable)
        {
            try
            {
                return ForEachNvidiaAudioController((set, data) =>
                {
                    var pcp = new SP_PROPCHANGE_PARAMS();
                    pcp.classInstallHeader.cbSize = Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
                    pcp.classInstallHeader.installFunction = (int)DIF_PROPERTYCHANGE;
                    pcp.stateChange = (int)(enable ? DICS_ENABLE : DICS_DISABLE);
                    pcp.scope = (int)DICS_FLAG_GLOBAL;
                    pcp.hwProfile = 0;
                    bool ok = SetupDiSetClassInstallParams(set, ref data, ref pcp, Marshal.SizeOf(pcp))
                              && SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data);
                    AppLogger.Log(ok
                        ? $"NVIDIA audio controller {(enable ? "enabled" : "disabled")}."
                        : $"NVIDIA audio controller change failed: Win32 error {Marshal.GetLastWin32Error()}");
                    return ok;
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"SetNvidiaAudioEnabled failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>True when any screen on the desktop is attached to an adapter whose name contains the text
        /// (e.g. an external monitor wired to the NVIDIA GPU). Null when the outputs cannot be listed.</summary>
        public static bool? IsDesktopScreenOnAdapter(string adapterNameSubstring)
        {
            try
            {
                bool any = false, found = false;
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(dd);
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                    any = true;
                    if ((dd.DeviceString ?? "").IndexOf(adapterNameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                        found = true;
                }
                return any ? found : null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"IsDesktopScreenOnAdapter failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// True when NVIDIA is the running display adapter and the Intel or AMD GPU is not.
        /// A BIOS dGPU-only boot leaves the iGPU in Device Manager with Availability Off Line.
        /// </summary>
        public static bool IsDiscreteGpuOnlyDisplay()
        {
            try
            {
                bool integratedRunning = false;
                bool discreteRunning = false;
                using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID, Availability FROM Win32_VideoController");
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        string id = (item["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                        bool running = Convert.ToInt32(item["Availability"] ?? 0) == 3;
                        if (id.Contains("VEN_8086") || id.Contains("VEN_1002"))
                            integratedRunning |= running;
                        else if (id.Contains("VEN_10DE"))
                            discreteRunning |= running;
                    }
                }

                if (discreteRunning || integratedRunning)
                    return discreteRunning && !integratedRunning;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"IsDiscreteGpuOnlyDisplay failed: {ex.Message}");
            }

            bool hasIntegrated = HasPresentDisplayDevice("VEN_8086") || HasPresentDisplayDevice("VEN_1002");
            bool hasDiscrete = HasPresentDisplayDevice("VEN_10DE");
            return hasDiscrete && !hasIntegrated;
        }

        public static bool HasPresentDisplayDevice(string hardwareIdSubstring)
        {
            Guid displayGuid = DisplayClassGuid;
            IntPtr devInfoSet = SetupDiGetClassDevs(ref displayGuid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == IntPtr.Zero || devInfoSet.ToInt64() == -1)
                return false;

            try
            {
                SP_DEVINFO_DATA devInfoData = new SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf(devInfoData);

                int memberIndex = 0;
                while (SetupDiEnumDeviceInfo(devInfoSet, memberIndex, ref devInfoData))
                {
                    memberIndex++;

                    StringBuilder buffer = new StringBuilder(1000);
                    if (SetupDiGetDeviceRegistryProperty(devInfoSet, ref devInfoData, 1, out _, buffer, buffer.Capacity, out _))
                    {
                        string hwId = buffer.ToString().ToUpperInvariant();
                        if (hwId.Contains(hardwareIdSubstring.ToUpperInvariant()))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"HasPresentDisplayDevice failed: {ex.Message}");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return false;
        }

    }
}
