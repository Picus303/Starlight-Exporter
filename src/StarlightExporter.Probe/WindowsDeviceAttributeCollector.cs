using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StarlightExporter.Official;

namespace StarlightExporter.Probe;

[SupportedOSPlatform("windows")]
internal sealed class WindowsDeviceAttributeCollector(
    IReadOnlyDictionary<string, string>? overrides = null) : IOfficialDeviceAttributeCollector
{
    private static readonly HashSet<string> UnknownStubs = new(StringComparer.Ordinal)
    {
        "bootRomVersion", "smcVersion", "board", "networkType", "proxyStatus",
        "batteryStatus", "chargeStatus", "appMemory", "hostname", "serialNumber",
        "IDFV", "screenBrightness", "buildTime", "appInstallTimeDiff",
        "appUpdateTimeDiff", "hasVpn", "packageName", "macosUUID",
    };

    public ValueTask<string?> CollectAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (overrides?.TryGetValue(name, out string? configured) == true)
        {
            return ValueTask.FromResult<string?>(configured);
        }
        if (!OfficialDeviceFingerprintFields.KnownNames.Contains(name))
        {
            return ValueTask.FromResult<string?>(null);
        }
        if (UnknownStubs.Contains(name))
        {
            return ValueTask.FromResult<string?>("unknown");
        }

        string? value = name switch
        {
            "osVersion" => WindowsOfficialRuntimeSources.SystemVersion(),
            "cpuName" => WindowsOfficialRuntimeSources.CpuRegistryValue("ProcessorNameString") ?? string.Empty,
            "cpuCores" => ProcessorCount().ToString(CultureInfo.InvariantCulture),
            "cpuFrequency" => CpuFrequency(),
            "gpuID" => Graphics()?.DeviceId.ToString(CultureInfo.InvariantCulture) ?? "1",
            "systemName" or "deviceName" => WindowsOfficialRuntimeSources.DeviceName(),
            "deviceUID" => PersistentMachineGuid(),
            "gpuName" => PersistentGpuName(),
            "gpuMemory" => VideoMemory(),
            "gpuVendorID" => Graphics()?.VendorId.ToString(CultureInfo.InvariantCulture) ?? "1",
            "memorySize" or "ramCapacity" => PhysicalMemory(available: false),
            "ramRemain" => PhysicalMemory(available: true),
            "screenSize" => ScreenSize(),
            "addressMAC" => MacAddress(),
            "deviceModel" => WindowsOfficialRuntimeSources.DeviceModel(),
            "deviceType" => "Desktop",
            "gpuAPI" => Graphics()?.Api ?? "unknown",
            "gpuVersion" => Graphics()?.FeatureVersion ?? "unknown",
            "gpuVendor" => Graphics()?.Vendor ?? "unknown",
            "isGpuMultiTread" => Graphics()?.DeviceCreationSucceeded == true ? "true" : "false",
            "packageVersion" => "2.53.0.196",
            "cpuType" => ProcessorType(),
            "romRemain" => DiskSpace(available: true),
            "romCapacity" => DiskSpace(available: false),
            "engineName" => "Qt",
            _ => null,
        };
        return ValueTask.FromResult(value);
    }

    public static OfficialPlayerDeviceInfo PlayerDeviceInfo()
    {
        WindowsGraphicsInfo? graphics = Graphics();
        string gpuName = graphics?.Description ?? string.Empty;
        string gpuVendor = graphics?.Vendor ?? string.Empty;
        return new OfficialPlayerDeviceInfo
        {
            OperatingSystem = WindowsOfficialRuntimeSources.UnityOperatingSystem(),
            DeviceModel = WindowsOfficialRuntimeSources.DeviceModel(),
            GraphicsDeviceName = gpuName,
            GraphicsDeviceType = graphics?.Api ?? "unknown",
            GraphicsDeviceVendor = gpuVendor,
            GraphicsDeviceVersion = graphics?.FeatureVersion ?? "unknown",
            GraphicsMemorySize = ParseUnsigned(VideoMemory()),
            ProcessorCount = ProcessorCount(),
            ProcessorFrequency = checked((uint)ParseUnsigned(CpuFrequency())),
            ProcessorType = WindowsOfficialRuntimeSources.CpuRegistryValue("ProcessorNameString")
                ?? string.Empty,
            SystemMemorySize = ParseUnsigned(PhysicalMemory(available: false)),
        };
    }

    private static ulong ParseUnsigned(string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed : 0;

    private static WindowsGraphicsInfo? Graphics() => WindowsGraphicsSources.FirstAdapter();

    private static string CpuFrequency()
    {
        object? value = WindowsOfficialRuntimeSources.CpuRegistryRawValue("~MHz");
        return value switch
        {
            int number when number >= 0 => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            _ => "0",
        };
    }

    private static string PersistentMachineGuid()
    {
        string value = WindowsOfficialRuntimeSources.ReadPersistentSdkValue("MIHOYOSDK_DEVICE_UID")
            ?? WindowsOfficialRuntimeSources.MachineGuid()
            ?? string.Empty;
        WindowsOfficialRuntimeSources.PersistSdkValue("MIHOYOSDK_DEVICE_UID", value);
        return value;
    }

    private static string PersistentGpuName()
    {
        string value = WindowsOfficialRuntimeSources.ReadPersistentSdkValue("MIHOYOSDK_GPU_NAME")
            ?? Graphics()?.Description
            ?? string.Empty;
        WindowsOfficialRuntimeSources.PersistSdkValue("MIHOYOSDK_GPU_NAME", value);
        return value;
    }

    private static string VideoMemory()
    {
        WindowsGraphicsInfo? graphics = Graphics();
        return graphics is null
            ? "1"
            : (graphics.DedicatedVideoMemory >> 20).ToString(CultureInfo.InvariantCulture);
    }

    private static string? PhysicalMemory(bool available)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((available ? status.AvailablePhysical : status.TotalPhysical) >> 20)
                .ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? ScreenSize()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || !GetWindowRect(foreground, out Rectangle window))
        {
            return null;
        }
        var work = new Rectangle();
        if (!SystemParametersInfo(48, 0, ref work, 0))
        {
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{work.Right - work.Left}x{window.Bottom - window.Top}");
    }

    private static string MacAddress()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!adapter.GetIPProperties().UnicastAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork
                        && !Equals(address.Address, System.Net.IPAddress.Any)))
                {
                    continue;
                }
                byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 0)
                {
                    return Convert.ToHexString(bytes);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return "FFFFFFFFFFFF";
    }

    private static string ProcessorType()
    {
        GetSystemInfo(out SystemInfo info);
        return info.ProcessorType.ToString(CultureInfo.InvariantCulture);
    }

    private static uint ProcessorCount()
    {
        GetSystemInfo(out SystemInfo info);
        return info.NumberOfProcessors;
    }

    private static string DiskSpace(bool available)
    {
        try
        {
            string root = Path.GetPathRoot(AppContext.BaseDirectory) ?? string.Empty;
            var drive = new DriveInfo(root);
            long bytes = available ? drive.TotalFreeSpace : drive.TotalSize;
            return (bytes >> 20).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            return "0";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public nint MinimumApplicationAddress;
        public nint MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void GetSystemInfo(out SystemInfo info);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rectangle rectangle);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint parameter, ref Rectangle value, uint flags);
}
