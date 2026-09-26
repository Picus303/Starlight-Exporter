using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StarlightExporter.Probe;

[SupportedOSPlatform("windows")]
internal sealed record WindowsGraphicsInfo(
    string Description,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemory,
    string Api,
    string FeatureVersion,
    bool DeviceCreationSucceeded)
{
    public string Vendor => VendorId switch
    {
        0x10DE or 0x12D2 => "NVIDIA",
        0x1002 => "AMD",
        0x163C or 0x8086 => "Intel",
        _ => "unknown",
    };
}

[SupportedOSPlatform("windows")]
internal static class WindowsGraphicsSources
{
    private const uint D3d11SdkVersion = 7;
    private const uint DxgiErrorNotFound = 0x887A0002;
    private static readonly Lazy<WindowsGraphicsInfo?> Cached = new(ReadFirstAdapter);

    public static WindowsGraphicsInfo? FirstAdapter() => Cached.Value;

    private static WindowsGraphicsInfo? ReadFirstAdapter()
    {
        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        try
        {
            Guid factoryId = typeof(IDXGIFactory1).GUID;
            int result = CreateDXGIFactory1(ref factoryId, out factory);
            if (result < 0 || factory is null)
            {
                return null;
            }
            result = factory.EnumAdapters1(0, out adapter);
            if (unchecked((uint)result) == DxgiErrorNotFound || result < 0 || adapter is null)
            {
                return null;
            }
            result = adapter.GetDesc1(out DxgiAdapterDescription description);
            if (result < 0)
            {
                return null;
            }

            (uint featureLevel, bool created) = CreateDevice(adapter);
            return new WindowsGraphicsInfo(
                description.Description.TrimEnd('\0'),
                description.VendorId,
                description.DeviceId,
                description.DedicatedVideoMemory.ToUInt64(),
                created ? FeatureApi(featureLevel) : "unknown",
                created ? FeatureVersion(featureLevel) : "unknown",
                created);
        }
        catch (Exception exception) when (exception is COMException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Release(adapter);
            Release(factory);
        }
    }

    private static (uint FeatureLevel, bool Created) CreateDevice(IDXGIAdapter1 adapter)
    {
        uint[] levels =
            [0xC100, 0xC000, 0xB100, 0xB000, 0xA100, 0xA000, 0x9300, 0x9200, 0x9100];
        nint device = 0;
        nint context = 0;
        try
        {
            int result = D3D11CreateDevice(
                adapter,
                driverType: 0,
                software: 0,
                flags: 0,
                levels,
                checked((uint)levels.Length),
                D3d11SdkVersion,
                out device,
                out uint selected,
                out context);
            return (selected, result >= 0);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException or COMException)
        {
            return (0, false);
        }
        finally
        {
            if (context != 0)
            {
                Marshal.Release(context);
            }
            if (device != 0)
            {
                Marshal.Release(device);
            }
        }
    }

    private static string FeatureApi(uint level) => level switch
    {
        0x9100 or 0x9200 or 0x9300 => "Direct3D 9.0c",
        0xA000 or 0xA100 => "Direct3D 10",
        0xB000 or 0xB100 => "Direct3D 11",
        0xC000 or 0xC100 => "Direct3D 12",
        _ => "unknown",
    };

    private static string FeatureVersion(uint level) => level switch
    {
        0x9100 => "Direct3D 9.1",
        0x9200 => "Direct3D 9.2",
        0x9300 => "Direct3D 9.3",
        0xA000 => "Direct3D 10.0",
        0xA100 => "Direct3D 10.1",
        0xB000 => "Direct3D 11.0",
        0xB100 => "Direct3D 11.1",
        0xC000 => "Direct3D 12.0",
        0xC100 => "Direct3D 12.1",
        _ => "unknown",
    };

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-086879011A05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid interfaceId, out nint parent);
        [PreserveSig] int EnumOutputs(uint output, out nint outputInterface);
        [PreserveSig] int GetDesc(out nint description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long userModeVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDescription description);
    }

    [ComImport]
    [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, nint unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid interfaceId, out nint parent);
        [PreserveSig] int EnumAdapters(uint adapter, out nint adapterInterface);
        [PreserveSig] int MakeWindowAssociation(nint window, uint flags);
        [PreserveSig] int GetWindowAssociation(out nint window);
        [PreserveSig] int CreateSwapChain(nint device, nint description, out nint swapChain);
        [PreserveSig] int CreateSoftwareAdapter(nint module, out nint adapter);
        [PreserveSig]
        int EnumAdapters1(
            uint adapter,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapterInterface);
        [PreserveSig] bool IsCurrent();
    }

    [DllImport("dxgi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [DllImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D11CreateDevice(
        [MarshalAs(UnmanagedType.Interface)] IDXGIAdapter1 adapter,
        uint driverType,
        nint software,
        uint flags,
        [In] uint[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint selectedFeatureLevel,
        out nint immediateContext);
}
