using Starlight.Protobuf.Core;

namespace StarlightExporter.Official;

public sealed record OfficialProfileSupplement(
    string Signature,
    uint PictureId,
    uint NameCardId);

public sealed record OfficialCaptureContext(
    uint OfficialUid,
    string Region,
    DateTimeOffset CapturedAtUtc,
    OfficialProfileSupplement? Profile = null);

public sealed record OfficialMessageEnvelope(
    long Sequence,
    IMessage Message);

public interface IOfficialMessageSource
{
    IAsyncEnumerable<OfficialMessageEnvelope> ReadAllAsync(
        CancellationToken cancellationToken = default);
}
