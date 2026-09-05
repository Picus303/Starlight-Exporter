using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public async Task MinimalFixtureDeserializesAndValidates()
    {
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));

        SnapshotValidationResult result = SnapshotValidator.Validate(snapshot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(765432100U, snapshot.Manifest.OfficialUid);
        Assert.Single(snapshot.Materials);
        Assert.Single(snapshot.Weapons);
        Assert.Single(snapshot.Avatars);
        Assert.Single(snapshot.Teams);
    }

    [Fact]
    public async Task InvalidReferencesAreReported()
    {
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("invalid-references.json"));

        SnapshotValidationResult result = SnapshotValidator.Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "AVATAR_WEAPON_MISSING");
        Assert.Contains(result.Errors, error => error.Code == "CURRENT_AVATAR_NOT_IN_TEAM");
        Assert.Contains(result.Errors, error => error.Code == "CURRENT_TEAM_MISSING");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData((long)uint.MaxValue + 1)]
    public async Task AvatarBornTimeMustFitInUInt32(long bornTime)
    {
        OfficialSnapshot source = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
        OfficialSnapshot snapshot = source with {
            Avatars = [source.Avatars[0] with { BornTime = bornTime }]
        };

        SnapshotValidationResult result = SnapshotValidator.Validate(snapshot);

        Assert.Contains(result.Errors, error => error.Code == "AVATAR_BORN_TIME_INVALID");
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
