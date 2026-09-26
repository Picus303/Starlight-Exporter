using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class SdkInstallationStateTests
{
    [Fact]
    public async Task DeviceIdentityAndServerFingerprintPersistAcrossRuns()
    {
        string path = Path.Combine(Path.GetTempPath(), $"starlight-sdk-{Guid.NewGuid():N}.json");
        try
        {
            SdkInstallationState initial = await SdkInstallationState.LoadOrCreateAsync(
                path, "synthetic-device", new FixedClock(), _ =>
                    Convert.FromHexString("0123456789abcdef"));
            Assert.Equal("synthetic-device1788566400123", initial.DeviceId);
            Assert.Equal("0123456789abcdef", initial.SeedId);
            Assert.Equal("1788566400123", initial.SeedTime);
            Assert.Null(initial.DeviceFingerprint);

            await initial.WithServerFingerprint("1234567890").SaveAsync(path);
            SdkInstallationState loaded = await SdkInstallationState.LoadOrCreateAsync(
                path, "a-different-device", new FixedClock());
            Assert.Equal(initial.DeviceId, loaded.DeviceId);
            Assert.Equal("1234567890", loaded.DeviceFingerprint);
            Assert.DoesNotContain(initial.DeviceId, loaded.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FreshIdentityMatchesDelivery4Fixture()
    {
        string unityId = UnityDeviceIdentifier.FromWindowsSerials(
            [" BOARD-001\t"], ["BIOS-002 "], [" OS-003"]);

        Assert.Equal("5f69dc346a4a33fa4007567bf04c0579b9668305", unityId);
        SdkInstallationState state = SdkInstallationState.Create(
            unityId,
            new FixtureClock(),
            _ => Convert.FromHexString("0123456789abcdef"));

        Assert.Equal(
            "5f69dc346a4a33fa4007567bf04c0579b96683051700000000123",
            state.DeviceId);
        Assert.Equal("0123456789abcdef", state.SeedId);
        Assert.Equal("1700000000123", state.SeedTime);
    }

    [Fact]
    public void UnityGuidFallbackHashesThePersistedTextIncludingBraces()
    {
        Guid guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        string result = UnityDeviceIdentifier.FromPersistedGuid(guid);

        Assert.Equal("54ed20a5fcf47004f68fac295c44264af0bdc80e", result);
    }

    [Fact]
    public async Task LegacyStateMigrationAndConcurrentCreationGenerateSeedsOnce()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"starlight-sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string legacyPath = Path.Combine(directory, "legacy.json");
            await File.WriteAllTextAsync(legacyPath, "{\"DeviceId\":\"legacy-device\"}");
            SdkInstallationState migrated = await SdkInstallationState.LoadOrCreateAsync(
                legacyPath,
                "ignored-device",
                new FixtureClock(),
                _ => Convert.FromHexString("0123456789abcdef"));
            Assert.Equal("legacy-device", migrated.DeviceId);
            Assert.Equal("0123456789abcdef", migrated.SeedId);

            int generatorCalls = 0;
            string concurrentPath = Path.Combine(directory, "concurrent.json");
            Func<int, byte[]> generator = length =>
            {
                Interlocked.Increment(ref generatorCalls);
                return Enumerable.Repeat((byte)0xAB, length).ToArray();
            };
            SdkInstallationState[] states = await Task.WhenAll(
                SdkInstallationState.LoadOrCreateAsync(
                    concurrentPath, "device", new FixtureClock(), generator),
                SdkInstallationState.LoadOrCreateAsync(
                    concurrentPath, "device", new FixtureClock(), generator));

            Assert.Equal(1, generatorCalls);
            Assert.Equal(states[0], states[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 0, 0, 0, 123, TimeSpan.Zero);
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(1700000000123);
    }
}
