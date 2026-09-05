using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class StarlightTargetMetadataTests
{
    [Fact]
    public void EmbeddedTargetLockIsAvailableToRuntimeReports()
    {
        StarlightTargetMetadata target = StarlightTargetMetadata.Current;

        Assert.Equal(expected: 1, target.SchemaVersion);
        Assert.Matches("^[0-9a-f]{40}$", target.StarlightCommit);
        Assert.Matches("^[0-9a-f]{40}$", target.ProtocolCommit);
        Assert.Equal(SnapshotContract.SupportedSourceProtocolVersion, target.ProtocolVersion);
    }

    [Theory]
    [InlineData(19, 0, 0)]
    [InlineData(20, 0, 1)]
    [InlineData(21, 1, 1)]
    [InlineData(80, 5, 6)]
    [InlineData(81, 6, 6)]
    public void PromotionPolicyExtendsStarlightCanonicalValueOnlyAtBreakpoints(
        uint level,
        uint expectedMinimum,
        uint expectedMaximum)
    {
        Assert.Equal(
            (expectedMinimum, expectedMaximum),
            StarlightTargetPolicy.PromotionRangeFor(level));
    }
}
