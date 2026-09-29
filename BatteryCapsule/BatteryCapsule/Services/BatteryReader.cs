using System.Management;
using System.Runtime.InteropServices;
using BatteryCapsule.Models;
using PowerLineStatus = BatteryCapsule.Models.PowerLineStatus;
namespace BatteryCapsule.Services;

/// <summary>
/// Reads raw battery telemetry directly from Windows via the battery class driver IOCTL
/// interface (the same low-level path the OS itself uses to build the taskbar battery icon).
/// No network access, no third-party services - everything here is local Win32/SetupAPI.
///
/// Reference: Microsoft's battery class driver IOCTLs (winioctl.h / batclass.h),
/// exposed through device interface GUID_DEVICEINTERFACE_BATTERY.
/// </summary>
public sealed class BatteryReader : IDisposable
{
    // {72631E54-78A4-11D0-BC7F-00AA00B7B32A} - GUID_DEVICEINTERFACE_BATTERY
    private static readonly Guid GUID_DEVICEINTERFACE_BATTERY =
        // NOTE: exact value from Microsoft's batclass.h - every hex digit matters;
        // a single transposed digit makes battery enumeration silently find nothing.
        new("72631e54-78a4-11d0-bcf7-00aa00b92a2f");

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    private const int FILE_DEVICE_BATTERY = 0x29;
    private const int METHOD_BUFFERED = 0;
    private const int FILE_ANY_ACCESS = 0;

    private static uint CtlCode(int deviceType, int function, int method, int access) =>
        (uint)((deviceType << 16) | (access << 14) | (function << 2) | method);

    private static readonly uint IOCTL_BATTERY_QUERY_TAG =
        CtlCode(FILE_DEVICE_BATTERY, 0x10, METHOD_BUFFERED, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_BATTERY_QUERY_INFORMATION =
        CtlCode(FILE_DEVICE_BATTERY, 0x11, METHOD_BUFFERED, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_BATTERY_QUERY_STATUS =
        CtlCode(FILE_DEVICE_BATTERY, 0x13, METHOD_BUFFERED, FILE_ANY_ACCESS);

    private enum BATTERY_QUERY_INFORMATION_LEVEL
    {
        BatteryInformation = 0,
        BatteryGranularityInformation = 1,
        BatteryTemperature = 2,
        BatteryEstimatedTime = 3,
        BatteryDeviceName = 4,
        BatteryManufactureDate = 5,
        BatteryManufactureName = 6,
        BatteryUniqueID = 7,
        BatterySerialNumber = 8
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public BATTERY_QUERY_INFORMATION_LEVEL InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    // BATTERY_STATUS.PowerState flags
    private const uint BATTERY_POWER_ON_LINE = 0x00000001;
    private const uint BATTERY_DISCHARGING = 0x00000002;
    private const uint BATTERY_CHARGING = 0x00000004;
    private const uint BATTERY_CRITICAL = 0x00000008;

    private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
    private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
        ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandleWrapper CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandleWrapper device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // Thin SafeHandle wrapper so P/Invoke handles get cleaned up reliably.
    private sealed class SafeFileHandleWrapper : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandleWrapper() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>
    /// Enumerates all present battery devices and reads their current raw telemetry.
    /// Returns an empty list on desktops / systems with no battery - callers should
    /// treat that as "no battery" rather than an error.
    /// </summary>
    public List<RawBatteryInfo> ReadAll()
    {
        var results = new List<RawBatteryInfo>();
        var guid = GUID_DEVICEINTERFACE_BATTERY;

        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
            return results;

        try
        {
            uint index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                bool found = SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData);
                if (!found) break; // no more devices

                string? path = GetDevicePath(deviceInfoSet, ref interfaceData);
                index++;

                if (path == null) continue;

                var info = ReadOneBattery(path);
                if (info != null)
                {
                    FillGapsFromWmi(info);
                    results.Add(info);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        if (results.Count == 0)
        {
            // Some firmware/drivers never expose the battery device interface
            // (SetupAPI finds nothing) even though Windows itself sees the battery
            // fine. In that case build the reading purely from WMI instead.
            var wmiOnly = ReadFromWmiOnly();
            if (wmiOnly != null) results.Add(wmiOnly);
        }

        return results;
    }

    private string? GetDevicePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        int requiredSize = 0;
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);
        if (requiredSize == 0) return null;

        IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // First 4 (or 8 on x64 due to packing) bytes of SP_DEVICE_INTERFACE_DETAIL_DATA is cbSize (DWORD).
            // On 64-bit the struct is packed such that cbSize must be set to 6 (a documented quirk),
            // on 32-bit it should be 5. We detect via IntPtr.Size.
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 6 : 5);

            int dummy = 0;
            bool ok = SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, ref dummy, IntPtr.Zero);
            if (!ok) return null;

            // Path string starts right after the cbSize DWORD.
            IntPtr pathPtr = IntPtr.Add(detailBuffer, 4);
            return Marshal.PtrToStringAuto(pathPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private RawBatteryInfo? ReadOneBattery(string devicePath)
    {
        using var handle = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) return null;

        // 1. Get the battery tag - required for all subsequent queries, changes when
        //    the battery is physically removed/replaced or the driver reinitializes.
        uint tag = QueryTag(handle);
        if (tag == 0) return null; // 0 = no battery currently present on this interface

        var info = new RawBatteryInfo { DeviceId = devicePath };

        // 2. Static BATTERY_INFORMATION (design capacity, full charge capacity, chemistry, cycle count)
        var battInfo = QueryInformation(handle, tag);
        if (battInfo.HasValue)
        {
            var bi = battInfo.Value;
            info.DesignedCapacityMWh = bi.DesignedCapacity == BATTERY_UNKNOWN_CAPACITY ? null : bi.DesignedCapacity;
            info.FullChargedCapacityMWh = bi.FullChargedCapacity == BATTERY_UNKNOWN_CAPACITY ? null : bi.FullChargedCapacity;
            info.CycleCount = bi.CycleCount;
            info.Chemistry = System.Text.Encoding.ASCII.GetString(bi.Chemistry).TrimEnd('\0');
        }

        // 3. Dynamic BATTERY_STATUS (capacity remaining, voltage, rate, charging state)
        var status = QueryStatus(handle, tag);
        if (status.HasValue)
        {
            var s = status.Value;
            info.PowerLine = (s.PowerState & BATTERY_POWER_ON_LINE) != 0
                ? PowerLineStatus.Online : PowerLineStatus.Offline;

            bool charging = (s.PowerState & BATTERY_CHARGING) != 0;
            bool discharging = (s.PowerState & BATTERY_DISCHARGING) != 0;

            info.RemainingCapacityMWh = s.Capacity == BATTERY_UNKNOWN_CAPACITY ? null : s.Capacity;
            info.VoltageMV = s.Voltage == BATTERY_UNKNOWN_VOLTAGE ? null : s.Voltage;
            info.RateMW = s.Rate == BATTERY_UNKNOWN_RATE ? null : s.Rate;

            if (charging) info.ChargeState = ChargeState.Charging;
            else if (discharging) info.ChargeState = ChargeState.Discharging;
            else if (info.PowerLine == PowerLineStatus.Online &&
                     info.RemainingCapacityMWh.HasValue && info.FullChargedCapacityMWh.HasValue &&
                     info.RemainingCapacityMWh.Value >= info.FullChargedCapacityMWh.Value)
                info.ChargeState = ChargeState.Full;
            else if (info.PowerLine == PowerLineStatus.Online)
                info.ChargeState = ChargeState.Idle;
            else
                info.ChargeState = ChargeState.Unknown;
        }

        // 4. Temperature - rarely supported; leave null (Unavailable) if the query fails.
        info.TemperatureC = QueryTemperature(handle, tag);

        return info;
    }

    /// <summary>
    /// WMI fallback: fills capacity/state fields the IOCTL path couldn't read.
    /// Win32_Battery is populated from the same driver data Windows' own battery UI
    /// uses, so values filled here are still Measured, never guessed. Only runs when
    /// the IOCTL path left gaps, so systems where IOCTL works pay no WMI cost.
    /// </summary>
    private static void FillGapsFromWmi(RawBatteryInfo info)
    {
        if (info.DesignedCapacityMWh.HasValue && info.FullChargedCapacityMWh.HasValue &&
            info.RemainingCapacityMWh.HasValue && info.ChargeState != ChargeState.Unknown)
        {
            LastWmiSummary = "not needed (IOCTL complete)";
            return; // nothing missing
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, DesignCapacity, FullChargeCapacity, BatteryStatus, Chemistry FROM Win32_Battery");
            int instances = 0;
            string detail = "no instances";
            foreach (ManagementObject mo in searcher.Get())
            {
                instances++;
                uint? design = ToUInt(mo["DesignCapacity"]);
                uint? full = ToUInt(mo["FullChargeCapacity"]);
                uint? pct = ToUInt(mo["EstimatedChargeRemaining"]);

                info.DesignedCapacityMWh ??= design;
                info.FullChargedCapacityMWh ??= full;
                if (!info.RemainingCapacityMWh.HasValue && pct.HasValue && full.HasValue && full.Value > 0)
                    info.RemainingCapacityMWh = (uint)Math.Round(full.Value * pct.Value / 100.0);

                if (info.ChargeState == ChargeState.Unknown)
                    info.ChargeState = MapWmiBatteryStatus(ToUInt(mo["BatteryStatus"]));

                if (string.IsNullOrEmpty(info.Chemistry))
                {
                    string? chem = MapWmiChemistry(ToUInt(mo["Chemistry"]));
                    if (chem != null) info.Chemistry = chem;
                }
                detail = $"estRemaining={pct?.ToString() ?? "null"}, design={design?.ToString() ?? "null"}, " +
                         $"full={full?.ToString() ?? "null"}, status={ToUInt(mo["BatteryStatus"])?.ToString() ?? "null"}";
                break; // first battery is enough
            }
            LastWmiSummary = $"instances={instances}, {detail}";
        }
        catch (Exception ex)
        {
            LastWmiSummary = $"query failed: {ex.GetType().Name}";
            /* WMI unavailable - gaps stay Unavailable, UI shows N/A honestly */
        }
    }

    /// <summary>What the last WMI fallback attempt found, for diagnostics.</summary>
    public static string LastWmiSummary { get; private set; } = "not attempted";

    private static uint? ToUInt(object? v)
    {
        if (v == null) return null;
        try { return Convert.ToUInt32(v); }
        catch { return null; }
    }

    /// <summary>
    /// Builds a battery reading purely from WMI, for machines where SetupAPI
    /// enumeration finds no battery device interfaces at all. Win32_Battery is
    /// the same driver data Windows' own battery UI uses, so this is Measured.
    /// Voltage/rate aren't exposed here, so power-based stats (time remaining)
    /// stay honestly N/A - but the percentage works.
    /// </summary>
    private static RawBatteryInfo? ReadFromWmiOnly()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, DesignCapacity, FullChargeCapacity, BatteryStatus, Chemistry FROM Win32_Battery");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint? pct = ToUInt(mo["EstimatedChargeRemaining"]);
                uint? design = ToUInt(mo["DesignCapacity"]);
                uint? full = ToUInt(mo["FullChargeCapacity"]);

                if (!pct.HasValue)
                {
                    LastWmiSummary = "wmi-only: instance found but no charge reading";
                    return null;
                }

                uint basis = full ?? design ?? 0;
                var info = new RawBatteryInfo
                {
                    DeviceId = "WMI:Win32_Battery",
                    DesignedCapacityMWh = design,
                    FullChargedCapacityMWh = full,
                    RemainingCapacityMWh = basis > 0
                        ? (uint)Math.Round(basis * pct.Value / 100.0)
                        : null,
                    ChargeState = MapWmiBatteryStatus(ToUInt(mo["BatteryStatus"])),
                    Chemistry = MapWmiChemistry(ToUInt(mo["Chemistry"])) ?? "",
                    PowerLine = PowerLineStatus.Unknown,
                    IsPresent = true,
                };
                LastWmiSummary = $"wmi-only: estRemaining={pct}, design={design?.ToString() ?? "null"}, " +
                                 $"full={full?.ToString() ?? "null"}, status={ToUInt(mo["BatteryStatus"])?.ToString() ?? "null"}";
                return info;
            }
            LastWmiSummary = "wmi-only: no Win32_Battery instances";
            return null;
        }
        catch (Exception ex)
        {
            LastWmiSummary = $"wmi-only query failed: {ex.GetType().Name}";
            return null;
        }
    }

    private static ChargeState MapWmiBatteryStatus(uint? status) => status switch
    {
        3 => ChargeState.Full,                                    // Fully Charged
        6 or 7 or 8 or 9 => ChargeState.Charging,                 // Charging*
        _ => ChargeState.Unknown                                  // anything else: don't guess
    };

    private static string? MapWmiChemistry(uint? chemistry) => chemistry switch
    {
        3 => "PbAc",   // Lead Acid
        4 => "NiCd",   // Nickel Cadmium
        5 => "NiMH",   // Nickel Metal Hydride
        6 => "Li-ion", // Lithium-ion
        8 => "Li-poly",// Lithium Polymer
        _ => null
    };

    private uint QueryTag(SafeFileHandleWrapper handle)
    {
        // Input: an "at rate" DWORD (0 for immediate query). Output: BatteryTag DWORD.
        IntPtr inBuf = Marshal.AllocHGlobal(4);
        IntPtr outBuf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(inBuf, 0);
            bool ok = DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, inBuf, 4, outBuf, 4, out int returned, IntPtr.Zero);
            return ok ? (uint)Marshal.ReadInt32(outBuf) : 0u;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private BATTERY_INFORMATION? QueryInformation(SafeFileHandleWrapper handle, uint tag)
    {
        var query = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = BATTERY_QUERY_INFORMATION_LEVEL.BatteryInformation,
            AtRate = 0
        };

        int inSize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
        int outSize = Marshal.SizeOf<BATTERY_INFORMATION>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, inBuf, false);
            bool ok = DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, inBuf, inSize, outBuf, outSize, out int returned, IntPtr.Zero);
            if (!ok) return null;
            return Marshal.PtrToStructure<BATTERY_INFORMATION>(outBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private BATTERY_STATUS? QueryStatus(SafeFileHandleWrapper handle, uint tag)
    {
        var wait = new BATTERY_WAIT_STATUS { BatteryTag = tag, Timeout = 0, PowerState = 0, LowCapacity = 0, HighCapacity = 0 };
        int inSize = Marshal.SizeOf<BATTERY_WAIT_STATUS>();
        int outSize = Marshal.SizeOf<BATTERY_STATUS>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(wait, inBuf, false);
            bool ok = DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, inBuf, inSize, outBuf, outSize, out int returned, IntPtr.Zero);
            if (!ok) return null;
            return Marshal.PtrToStructure<BATTERY_STATUS>(outBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private double? QueryTemperature(SafeFileHandleWrapper handle, uint tag)
    {
        var query = new BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = BATTERY_QUERY_INFORMATION_LEVEL.BatteryTemperature,
            AtRate = 0
        };
        int inSize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.StructureToPtr(query, inBuf, false);
            bool ok = DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, inBuf, inSize, outBuf, 4, out int returned, IntPtr.Zero);
            if (!ok) return null; // most firmware doesn't support this level - not an error, just unavailable

            // Value is in tenths of a degree Kelvin.
            uint tenthsKelvin = (uint)Marshal.ReadInt32(outBuf);
            if (tenthsKelvin == 0) return null;
            double kelvin = tenthsKelvin / 10.0;
            return Math.Round(kelvin - 273.15, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    public void Dispose() { }
}
