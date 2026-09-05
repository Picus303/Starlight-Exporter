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

    [Fact]
    public async Task SnapshotWriterProducesCanonicalRoundTrippableJson()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            OfficialSnapshot source = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
            var addedMaterial = new SnapshotMaterial(ItemId: 2002, Guid: 20002, Count: 2);
            var firstUnsupported = new UnsupportedRecord("quest", "2", "Not persisted.");
            var secondUnsupported = new UnsupportedRecord("achievement", "1", "Not persisted.");
            OfficialSnapshot first = source with {
                Materials = [addedMaterial, .. source.Materials],
                Unsupported = [firstUnsupported, secondUnsupported]
            };
            OfficialSnapshot second = source with {
                Materials = [.. source.Materials, addedMaterial],
                Unsupported = [secondUnsupported, firstUnsupported]
            };
            string firstPath = Path.Combine(testDirectory, "first.snapshot.json");
            string secondPath = Path.Combine(testDirectory, "second.snapshot.json");

            await OfficialSnapshotSerializer.WriteNewAsync(firstPath, first);
            await OfficialSnapshotSerializer.WriteNewAsync(secondPath, second);

            Assert.Equal(await File.ReadAllBytesAsync(firstPath), await File.ReadAllBytesAsync(secondPath));
            OfficialSnapshot roundTripped = await OfficialSnapshotSerializer.ReadAsync(firstPath);
            Assert.Equal([1001u, 2002u], roundTripped.Materials.Select(item => item.ItemId));
            Assert.Equal(["achievement", "quest"], roundTripped.Unsupported.Select(item => item.Category));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotWriterRefusesOverwriteAndPreservesExistingFile()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string outputPath = Path.Combine(testDirectory, "existing.snapshot.json");
            await File.WriteAllTextAsync(outputPath, "keep");
            OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));

            await Assert.ThrowsAsync<IOException>(() =>
                OfficialSnapshotSerializer.WriteNewAsync(outputPath, snapshot));

            Assert.Equal("keep", await File.ReadAllTextAsync(outputPath));
            Assert.Single(Directory.EnumerateFiles(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledSnapshotWriteLeavesNoArtifact()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string outputPath = Path.Combine(testDirectory, "cancelled.snapshot.json");
            OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                OfficialSnapshotSerializer.WriteNewAsync(outputPath, snapshot, cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("account_token")]
    [InlineData("password")]
    [InlineData("cookie")]
    [InlineData("authorizationHeader")]
    [InlineData("privateKey")]
    [InlineData("officialCredential")]
    [InlineData("clientSecret")]
    [InlineData("session_key")]
    public void SensitiveJsonPropertyIsRejected(string propertyName)
    {
        byte[] unsafeDocument = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"{propertyName}\":\"must-not-leak\"}}");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            SnapshotSecurityGuard.EnsureNoSensitiveProperties(unsafeDocument));

        Assert.Contains(propertyName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSnapshotIsRejectedBeforeDeserialization()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string path = Path.Combine(testDirectory, "oversized.snapshot.json");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(SnapshotContract.MaximumDocumentBytes + 1);
            }

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                OfficialSnapshotSerializer.ReadAsync(path));

            Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotEntityLimitsAreValidated()
    {
        OfficialSnapshot source = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
        OfficialSnapshot oversized = source with {
            Materials = [.. Enumerable.Range(1, SnapshotContract.MaximumMaterials + 1)
                .Select(index => new SnapshotMaterial((uint)index, (ulong)index, 1))]
        };

        SnapshotValidationResult result = SnapshotValidator.Validate(oversized);

        Assert.Contains(result.Errors, error => error.Code == "MATERIAL_LIMIT_EXCEEDED");
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
