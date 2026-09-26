using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using StarlightExporter.Official;

namespace StarlightExporter.Probe;

[SupportedOSPlatform("windows")]
internal static class WindowsOfficialRuntimeSources
{
    private const string PrimarySdkPath = @"Software\miHoYoSDK";
    private const string LegacySdkPath = @"Software\miHoYo\miHoYoSDK";
    private const string UnityDevicePath = @"Software\Unity Technologies\DeviceId";

    public static string UnityDeviceId()
    {
        string fromWmi = UnityDeviceIdentifier.FromWindowsSerials(
            QueryWmi("Win32_BaseBoard", "SerialNumber"),
            QueryWmi("Win32_BIOS", "SerialNumber"),
            QueryWmi("Win32_OperatingSystem", "SerialNumber"));
        if (!string.IsNullOrEmpty(fromWmi))
        {
            return fromWmi;
        }

        return UnityDeviceIdentifier.FromPersistedGuid(ReadOrCreateUnityGuid());
    }

    public static string SystemVersion()
    {
        (uint major, _, uint build) = WindowsVersion();
        if (major == 10)
        {
            return build >= 22000 ? "Windows 11" : "Windows 10";
        }
        return string.Empty;
    }

    public static string UnityOperatingSystem()
    {
        (uint major, uint minor, uint build) = WindowsVersion();
        string name = major == 10 && build >= 22000
            ? "Windows 11"
            : major == 10 ? "Windows 10" : "Windows";
        string bits = Environment.Is64BitOperatingSystem ? "64bit" : "32bit";
        return string.Create(CultureInfo.InvariantCulture,
            $"{name}  ({major}.{minor}.{build}) {bits}");
    }

    public static string DeviceModel() =>
        ReadRegistry(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct")
        ?? string.Empty;

    public static string? CpuRegistryValue(string name) =>
        ReadRegistry(Registry.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", name);

    public static object? CpuRegistryRawValue(string name) =>
        ReadRegistryRaw(Registry.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", name);

    public static string? FirstVideoControllerValue(string propertyName) =>
        QueryWmi("Win32_VideoController", propertyName).FirstOrDefault(value => value is not null);

    public static string? FirstComputerSystemValue(string propertyName) =>
        QueryWmi("Win32_ComputerSystem", propertyName).FirstOrDefault(value => value is not null);

    public static string? ReadPersistentSdkValue(string name) => ReadSdkValue(name);

    public static string? MachineGuid() =>
        ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Cryptography", "MachineGuid");

    public static void PersistSdkValue(string name, string value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PrimarySdkPath, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            // DeviceFp defines empty-string retry behavior for these auxiliary caches.
        }
    }

    public static string DeviceName()
    {
        char[] buffer = new char[16];
        uint length = 16;
        if (GetComputerName(buffer, ref length))
        {
            return new string(buffer, 0, checked((int)length));
        }
        return string.Empty;
    }

    public static (int Width, int Height) DisplayDimensions() =>
        (GetSystemMetrics(0), GetSystemMetrics(1));

    public static SdkInstallationState? ReadSdkState()
    {
        string? deviceId = ReadSdkValue("MIHOYOSDK_DEVICE_ID");
        string? seedId = ReadSdkValue("MIHOYOSDK_SEED_ID");
        string? seedTime = ReadSdkValue("MIHOYOSDK_SEED_TIME");
        if (!ValidDeviceId(deviceId)
            || seedId is not { Length: 16 }
            || !seedId.All(character => char.IsAsciiHexDigit(character)
                && !char.IsAsciiLetterUpper(character))
            || seedTime is not { Length: > 0 and <= 32 }
            || !seedTime.All(char.IsAsciiDigit))
        {
            return null;
        }

        string? fingerprint = ReadSdkValue("MIHOYOSDK_DEVICE_FP");
        return new SdkInstallationState
        {
            DeviceId = deviceId!,
            SeedId = seedId,
            SeedTime = seedTime,
            DeviceFingerprint = SdkInstallationState.IsValidFingerprint(fingerprint) ? fingerprint : null,
        };
    }

    private static bool ValidDeviceId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && string.Equals(Uri.EscapeDataString(value), value, StringComparison.Ordinal);

    public static void PersistSdkState(SdkInstallationState state)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PrimarySdkPath, writable: true);
            key.SetValue("MIHOYOSDK_DEVICE_ID", state.DeviceId, RegistryValueKind.String);
            key.SetValue("MIHOYOSDK_SEED_ID", state.SeedId, RegistryValueKind.String);
            key.SetValue("MIHOYOSDK_SEED_TIME", state.SeedTime, RegistryValueKind.String);
            if (state.DeviceFingerprint is not null)
            {
                key.SetValue("MIHOYOSDK_DEVICE_FP", state.DeviceFingerprint, RegistryValueKind.String);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            throw new ProbeConfigurationException("The SDK installation state could not be persisted.");
        }
    }

    private static string?[] QueryWmi(string className, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {propertyName} FROM {className}");
            using ManagementObjectCollection results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(item => item[propertyName]?.ToString())
                .ToArray();
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static Guid ReadOrCreateUnityGuid()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(UnityDevicePath, writable: true);
            if (key.GetValue(null) is string existing && Guid.TryParse(existing, out Guid parsed))
            {
                return parsed;
            }

            Guid created = Guid.NewGuid();
            key.SetValue(null, created.ToString("B"), RegistryValueKind.String);
            return created;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            return Guid.Empty;
        }
    }

    private static string? ReadSdkValue(string name) =>
        ReadRegistry(Registry.CurrentUser, PrimarySdkPath, name)
        ?? ReadRegistry(Registry.CurrentUser, LegacySdkPath, name);

    private static string? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static object? ReadRegistryRaw(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path, writable: false);
            return key?.GetValue(name);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static (uint Major, uint Minor, uint Build) WindowsVersion()
    {
        RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);
        return (major, minor, build & 0xFFFF);
    }

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void RtlGetNtVersionNumbers(
        out uint majorVersion,
        out uint minorVersion,
        out uint buildNumber);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComputerName([Out] char[] buffer, ref uint size);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int index);
}
