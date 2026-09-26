using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StarlightExporter.Official;

namespace StarlightExporter.Probe;

internal sealed record PreparedSdkDevice(string UniqueIdentifier, bool RegistryStateImported);

internal sealed record OfficialProbeRuntimeConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public string? DeviceId { get; init; }
    public string? DeviceUniqueIdentifier { get; init; }
    public string? DeviceFingerprint { get; init; }
    public string? SeedId { get; init; }
    public string? SeedTime { get; init; }
    public string Platform { get; init; } = "3";
    public string AppName { get; init; } = "hk4e_global";
    public string Language { get; init; } = "fr";
    public uint ClientType { get; init; } = 3;
    public string SystemVersion { get; init; } = string.Empty;
    public string DeviceModel { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public Dictionary<string, string> ExtFields { get; init; } = [];
    public string? DeviceInfo { get; init; }
    public string? SecurityLibraryPath { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    public static OfficialProbeRuntimeConfig Load(string path, bool requireAuthHeaders = true)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 65536)
        {
            throw new ProbeConfigurationException("The local runtime file is absent or too large.");
        }
        OfficialProbeRuntimeConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<OfficialProbeRuntimeConfig>(
                File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            throw new ProbeConfigurationException("The local runtime file has an invalid schema.");
        }
        if (config is null
            || !string.Equals(config.Platform, "3", StringComparison.Ordinal)
            || !string.Equals(config.AppName, "hk4e_global", StringComparison.Ordinal)
            || (requireAuthHeaders && (!string.Equals(config.Language, "fr", StringComparison.Ordinal)
                || config.ClientType != 3))
            || config.ExtFields is null
            || config.ExtFields.Count > 64
            || config.ExtFields.Any(pair => !Valid(pair.Key) || pair.Value is null
                || pair.Value.Length > 4096))
        {
            throw new ProbeConfigurationException("The local runtime file is incomplete or invalid.");
        }
        if (OperatingSystem.IsWindows())
        {
            config = config with
            {
                SystemVersion = string.IsNullOrEmpty(config.SystemVersion)
                    ? WindowsOfficialRuntimeSources.SystemVersion() : config.SystemVersion,
                DeviceModel = string.IsNullOrEmpty(config.DeviceModel)
                    ? WindowsOfficialRuntimeSources.DeviceModel() : config.DeviceModel,
                DeviceName = string.IsNullOrEmpty(config.DeviceName)
                    ? WindowsOfficialRuntimeSources.DeviceName() : config.DeviceName,
                DeviceInfo = string.IsNullOrEmpty(config.DeviceInfo)
                    ? WindowsDeviceAttributeCollector.PlayerDeviceInfo().Serialize()
                    : config.DeviceInfo,
                ScreenWidth = config.ScreenWidth > 0
                    ? config.ScreenWidth : WindowsOfficialRuntimeSources.DisplayDimensions().Width,
                ScreenHeight = config.ScreenHeight > 0
                    ? config.ScreenHeight : WindowsOfficialRuntimeSources.DisplayDimensions().Height,
            };
        }
        if (config.DeviceId is not null && !Valid(config.DeviceId))
        {
            throw new ProbeConfigurationException("The SDK device ID is invalid.");
        }
        if (config.SeedId is not null && !ValidSeedId(config.SeedId))
        {
            throw new ProbeConfigurationException("The configured SDK seed ID is invalid.");
        }
        if (config.SeedTime is not null && !ValidSeedTime(config.SeedTime))
        {
            throw new ProbeConfigurationException("The configured SDK seed time is invalid.");
        }
        if (config.DeviceFingerprint is not null
            && !SdkInstallationState.IsValidFingerprint(config.DeviceFingerprint))
        {
            throw new ProbeConfigurationException("The saved SDK device fingerprint is invalid.");
        }
        return config;
    }

    public async Task<PreparedSdkDevice> PrepareDeviceAsync(
        string statePath,
        CancellationToken cancellationToken)
    {
        string uniqueIdentifier;
        if (Valid(DeviceUniqueIdentifier))
        {
            uniqueIdentifier = DeviceUniqueIdentifier!;
        }
        else if (OperatingSystem.IsWindows())
        {
            uniqueIdentifier = WindowsOfficialRuntimeSources.UnityDeviceId();
        }
        else
        {
            throw new ProbeConfigurationException("The Unity device identifier is unavailable.");
        }

        SdkInstallationState? registryState = OperatingSystem.IsWindows()
            ? WindowsOfficialRuntimeSources.ReadSdkState()
            : null;
        bool registryStateImported = false;
        if (!File.Exists(statePath) && registryState is not null)
        {
            await registryState.SaveAsync(statePath, cancellationToken);
            registryStateImported = true;
        }

        if (!File.Exists(statePath) && DeviceId is not null)
        {
            SdkInstallationState generated = SdkInstallationState.Create(uniqueIdentifier);
            var configured = generated with
            {
                DeviceId = DeviceId,
                SeedId = SeedId ?? generated.SeedId,
                SeedTime = SeedTime ?? generated.SeedTime,
                DeviceFingerprint = DeviceFingerprint,
            };
            await configured.SaveAsync(statePath, cancellationToken);
        }

        SdkInstallationState state = await SdkInstallationState.LoadOrCreateAsync(
            statePath, uniqueIdentifier, cancellationToken: cancellationToken);
        if (registryState is not null)
        {
            SdkInstallationState reconciled = ReconcilePersistedState(state, registryState);
            if (reconciled != state)
            {
                state = reconciled;
                await state.SaveAsync(statePath, cancellationToken);
                registryStateImported = true;
            }
        }
        if (DeviceId is not null
            && !string.Equals(state.DeviceId, DeviceId, StringComparison.Ordinal))
        {
            throw new ProbeConfigurationException(
                "The persisted SDK device ID differs from the selected installation.");
        }
        if (DeviceFingerprint is not null
            && !string.Equals(state.DeviceFingerprint, DeviceFingerprint, StringComparison.Ordinal))
        {
            state = state.WithServerFingerprint(DeviceFingerprint);
            await state.SaveAsync(statePath, cancellationToken);
        }
        if (OperatingSystem.IsWindows())
        {
            WindowsOfficialRuntimeSources.PersistSdkState(state);
        }
        return new PreparedSdkDevice(uniqueIdentifier, registryStateImported);
    }

    internal static SdkInstallationState ReconcilePersistedState(
        SdkInstallationState local,
        SdkInstallationState registry)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(registry);
        bool sameIdentity = string.Equals(local.DeviceId, registry.DeviceId, StringComparison.Ordinal)
            && string.Equals(local.SeedId, registry.SeedId, StringComparison.Ordinal)
            && string.Equals(local.SeedTime, registry.SeedTime, StringComparison.Ordinal);
        if (!sameIdentity)
        {
            return registry;
        }

        return SdkInstallationState.IsValidFingerprint(registry.DeviceFingerprint)
            ? local.WithDeviceFingerprint(registry.DeviceFingerprint!)
            : local;
    }

    public OfficialDeviceFingerprintRuntime FingerprintRuntime()
    {
        if (!string.Equals(Platform, "3", StringComparison.Ordinal))
        {
            throw new ProbeConfigurationException("The 7.0 DeviceFp platform must be 3.");
        }
        return new OfficialDeviceFingerprintRuntime();
    }

    public OfficialSdkRequestHeaders SdkHeaders() => new()
    {
        DeviceId = string.Empty,
        DeviceFingerprint = string.Empty,
        Language = Language,
        AppId = OfficialClientProfile.OsGlobalV70.ApplicationId,
        ClientType = ClientType,
        GameBiz = AppName,
        ChannelId = OfficialClientProfile.OsGlobalV70.ChannelId,
        SdkVersion = "2.53.0.196",
        MdkVersion = "2.53.0.196",
        ChannelVersion = "2.53.0.196",
        SystemVersion = SystemVersion,
        DeviceModel = DeviceModel,
        DeviceName = DeviceName,
    };

    public OfficialPlayerLoginProfile LoginProfile(SdkInstallationState installation)
    {
        if (!ValidDeviceInfo(DeviceInfo))
        {
            throw new ProbeConfigurationException("The local runtime file lacks deviceInfo for PlayerLogin.");
        }
        string securityLibraryMd5 = string.Empty;
        if (!string.IsNullOrWhiteSpace(SecurityLibraryPath))
        {
            if (!File.Exists(SecurityLibraryPath))
            {
                throw new ProbeConfigurationException("The configured security library is unavailable.");
            }
            using FileStream library = File.OpenRead(SecurityLibraryPath);
#pragma warning disable CA5351 // The observed protocol requires MD5 as a file identity marker.
            securityLibraryMd5 = Convert.ToHexStringLower(MD5.HashData(library));
#pragma warning restore CA5351
        }
        return new OfficialPlayerLoginProfile
        {
            DeviceInfo = DeviceInfo!,
            DeviceName = DeviceName,
            DeviceUuid = installation.DeviceId,
            DeviceFingerprint = installation.DeviceFingerprint ?? string.Empty,
            SystemVersion = SystemVersion,
            SecurityLibraryMd5 = securityLibraryMd5,
            ScreenWidth = ScreenWidth,
            ScreenHeight = ScreenHeight,
        };
    }

    public void RequireLoginInputs()
    {
        if (!ValidDeviceInfo(DeviceInfo)
            || ScreenWidth <= 0
            || ScreenHeight <= 0
            || (!string.IsNullOrWhiteSpace(SecurityLibraryPath)
                && !File.Exists(SecurityLibraryPath)))
        {
            throw new ProbeConfigurationException("The local runtime file lacks valid PlayerLogin inputs.");
        }
    }

    public override string ToString() => "OfficialProbeRuntimeConfig { Machine = [REDACTED] }";

    private static bool Valid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        && !value.Contains('\r') && !value.Contains('\n');

    private static bool ValidDeviceInfo(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && !value.Contains('\r') && !value.Contains('\n');

    private static bool ValidSeedId(string value) =>
        value.Length == 16
        && value.All(character => char.IsAsciiHexDigit(character)
            && !char.IsAsciiLetterUpper(character));

    private static bool ValidSeedTime(string value) =>
        value.Length is > 0 and <= 32 && value.All(char.IsAsciiDigit);
}

internal sealed class ConfiguredDeviceAttributeCollector(Dictionary<string, string> attributes)
    : IOfficialDeviceAttributeCollector
{
    public IReadOnlyList<string> MissingNames => _missingNames;
    private readonly List<string> _missingNames = [];
    private readonly WindowsDeviceAttributeCollector? _windows = OperatingSystem.IsWindows()
        ? new WindowsDeviceAttributeCollector(attributes)
        : null;

    public ValueTask<string?> CollectAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attributes.TryGetValue(name, out string? value))
        {
            return ValueTask.FromResult<string?>(value);
        }
        if (OperatingSystem.IsWindows() && _windows is not null)
        {
            return CollectWindowsAsync(name, cancellationToken);
        }
        _missingNames.Add(name);
        return ValueTask.FromResult<string?>(null);
    }

    [SupportedOSPlatform("windows")]
    private async ValueTask<string?> CollectWindowsAsync(string name, CancellationToken cancellationToken)
    {
        string? value = await _windows!.CollectAsync(name, cancellationToken);
        if (value is null)
        {
            _missingNames.Add(name);
        }
        return value;
    }
}

internal sealed class ProbeConfigurationException(string message) : Exception(message);
