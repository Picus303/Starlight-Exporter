using Starlight.Gate.Crypto;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class OfficialGateCompatibilityTests
{
    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(987654321ul)]
    [InlineData(ulong.MaxValue)]
    public void SessionPadGeneratorMatchesPinnedStarlightGate(ulong seed)
    {
        Assert.Equal(MtKey.Generate(seed), OfficialGateKeySchedule.GenerateSessionPad(seed));
    }
}
