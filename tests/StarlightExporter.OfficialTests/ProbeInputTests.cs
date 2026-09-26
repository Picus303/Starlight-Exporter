using System.Runtime.Versioning;
using StarlightExporter.Official;
using StarlightExporter.Probe;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class ProbeInputTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions = new(
        System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void CredentialsFileRequiresOnlyEmailAndPasswordAndKeepsDelimiterInPassword()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-credentials-{Guid.NewGuid():N}.env");
        try
        {
            File.WriteAllText(path,
                "STARLIGHT_EXPORTER_OFFICIAL_EMAIL=example@test.invalid\n"
                + "STARLIGHT_EXPORTER_OFFICIAL_PASSWORD='synthetic=secret'\n"
                + "STARLIGHT_EXPORTER_OFFICIAL_REGION=os_euro\n");

            ProbeCredentialFile result = ProbeCredentialFile.Load(path);

            Assert.Equal("example@test.invalid", result.Credentials.Account);
            Assert.Equal("synthetic=secret", result.Credentials.Password);
            Assert.Equal("os_euro", result.RegionName);
            Assert.DoesNotContain("synthetic=secret", result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RuntimeConfigurationUsesThePinnedD4ProfileWithoutManualIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-runtime-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"deviceUniqueIdentifier\":\"synthetic\",\"extFields\":{}}");

            OfficialProbeRuntimeConfig config = OfficialProbeRuntimeConfig.Load(path);

            Assert.Equal("3", config.Platform);
            Assert.Equal("hk4e_global", config.AppName);
            Assert.Equal("fr", config.Language);
            Assert.Equal(3u, config.ClientType);
            if (OperatingSystem.IsWindows())
            {
                Assert.StartsWith("operatingSystem:", config.DeviceInfo, StringComparison.Ordinal);
                Assert.EndsWith("&platformdetail:WinST", config.DeviceInfo,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlayerDeviceInfoUsesTheDocumentedTwelvePartOrder()
    {
        string serialized = new OfficialPlayerDeviceInfo
        {
            OperatingSystem = "Windows 11 (synthetic)",
            DeviceModel = "SYNTH-BOARD",
            GraphicsDeviceName = "SYNTH-GPU",
            GraphicsDeviceType = "Direct3D12",
            GraphicsDeviceVendor = "SYNTH-VENDOR",
            GraphicsDeviceVersion = "SYNTH-DRIVER",
            GraphicsMemorySize = 8192,
            ProcessorCount = 16,
            ProcessorFrequency = 3600,
            ProcessorType = "SYNTH-CPU",
            SystemMemorySize = 32768,
        }.Serialize();

        Assert.Equal(
            "operatingSystem:Windows 11 (synthetic)"
            + "&deviceModel:SYNTH-BOARD"
            + "&graphicsDeviceName:SYNTH-GPU"
            + "&graphicsDeviceType:Direct3D12"
            + "&graphicsDeviceVendor:SYNTH-VENDOR"
            + "&graphicsDeviceVersion:SYNTH-DRIVER"
            + "&graphicsMemorySize:8192"
            + "&processorCount:16"
            + "&processorFrequency:3600"
            + "&processorType:SYNTH-CPU"
            + "&systemMemorySize:32768"
            + "&platformdetail:WinST",
            serialized);
    }

    [Fact]
    public void RegistryFingerprintSupersedesTheLocalFallbackForTheSameIdentity()
    {
        var local = new SdkInstallationState
        {
            DeviceId = "synthetic-device",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
            DeviceFingerprint = "1111111111",
        };
        SdkInstallationState registry = local with { DeviceFingerprint = "2222222222" };

        SdkInstallationState result = OfficialProbeRuntimeConfig.ReconcilePersistedState(
            local, registry);

        Assert.Equal(registry, result);
    }

    [Fact]
    public void RegistryIdentitySupersedesAStaleLocalInstallationWithoutCarryingItsFingerprint()
    {
        var local = new SdkInstallationState
        {
            DeviceId = "stale-device",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
            DeviceFingerprint = "1111111111",
        };
        var registry = new SdkInstallationState
        {
            DeviceId = "official-device",
            SeedId = "fedcba9876543210",
            SeedTime = "456",
        };

        SdkInstallationState result = OfficialProbeRuntimeConfig.ReconcilePersistedState(
            local, registry);

        Assert.Equal(registry, result);
        Assert.Null(result.DeviceFingerprint);
    }

    [Fact]
    public void SdkPreflightDoesNotRequirePasswordHeaderOrGateProfileValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-runtime-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "deviceId":"synthetic-device",
                  "deviceFingerprint":"1234567890",
                  "seedId":"0123456789abcdef",
                  "seedTime":"123",
                  "platform":"3",
                  "appName":"hk4e_global",
                  "language":"fr",
                  "clientType":3,
                  "systemVersion":"",
                  "deviceModel":"",
                  "deviceName":"",
                  "extFields":{}
                }
                """);

            var parsed = System.Text.Json.JsonSerializer.Deserialize<OfficialProbeRuntimeConfig>(
                File.ReadAllText(path), WebJsonOptions);
            Assert.Equal("synthetic-device", parsed?.DeviceId);
            Assert.Equal("0123456789abcdef", parsed?.SeedId);
            Assert.Equal("123", parsed?.SeedTime);

            Assert.NotNull(OfficialProbeRuntimeConfig.Load(path, requireAuthHeaders: false));
            Assert.NotNull(OfficialProbeRuntimeConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DeviceFingerprintCollectorCoversAllDelivery4NamesAsStrings()
    {
        Assert.Equal(45, OfficialDeviceFingerprintFields.KnownNames.Count);
        var values = OfficialDeviceFingerprintFields.KnownNames.ToDictionary(
            name => name, _ => string.Empty, StringComparer.Ordinal);
        var collector = new WindowsDeviceAttributeCollector(values);

        foreach (string name in OfficialDeviceFingerprintFields.KnownNames)
        {
            Assert.Equal(string.Empty, await collector.CollectAsync(name));
        }
        Assert.Null(await collector.CollectAsync("futureField"));

        var nativeFallbacks = new WindowsDeviceAttributeCollector();
        Assert.Equal("unknown", await nativeFallbacks.CollectAsync("serialNumber"));
        Assert.Equal("unknown", await nativeFallbacks.CollectAsync("macosUUID"));
        Assert.Equal("Desktop", await nativeFallbacks.CollectAsync("deviceType"));
        Assert.Equal("Qt", await nativeFallbacks.CollectAsync("engineName"));
        Assert.Equal("2.53.0.196", await nativeFallbacks.CollectAsync("packageVersion"));
    }
}
