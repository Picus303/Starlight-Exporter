using Starlight.Protobuf.Core;

namespace StarlightExporter.Official;

public sealed class OfficialSnapshotReadiness(bool loginAccepted = false)
{
    private readonly OfficialSnapshotCache _cache = new();

    public bool LoginAccepted { get; private set; } = loginAccepted;
    public bool SnapshotCoverage => LoginAccepted && _cache.HasCoverage;
    public bool BoundaryValidated { get; private set; }
    public bool IsReady => SnapshotCoverage && _cache.HasValidInvariants && BoundaryValidated;

    public void MarkLoginAccepted() => LoginAccepted = true;

    public void MarkCaptureBoundaryValidated()
    {
        if (SnapshotCoverage && _cache.HasValidInvariants)
        {
            BoundaryValidated = true;
        }
    }

    public void Observe(IMessage message)
    {
        _cache.Observe(message);
        BoundaryValidated = false;
    }
}
