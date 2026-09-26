using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public enum OfficialProfileFieldState
{
    Unknown,
    Absent,
    Present,
}

public readonly record struct OfficialProfileField<T>(OfficialProfileFieldState State, T Value);

public sealed class OfficialSocialProfileTracker(uint playerUid)
{
    public OfficialProfileField<string> Signature { get; private set; } =
        new(OfficialProfileFieldState.Unknown, string.Empty);
    public OfficialProfileField<uint> PictureId { get; private set; } =
        new(OfficialProfileFieldState.Unknown, 0);
    public OfficialProfileField<uint> NameCardId { get; private set; } =
        new(OfficialProfileFieldState.Unknown, 0);

    public bool DetailAccepted { get; private set; }

    public void Observe(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case GetPlayerSocialDetailRsp response when response.Retcode == 0
                && response.DetailData is { } detail
                && detail.Uid == playerUid:
                DetailAccepted = true;
                Signature = new(
                    string.IsNullOrEmpty(detail.Signature)
                        ? OfficialProfileFieldState.Absent
                        : OfficialProfileFieldState.Present,
                    detail.Signature);
                uint pictureId = detail.ProfilePicture?.PictureId ?? 0;
                PictureId = new(
                    pictureId == 0 ? OfficialProfileFieldState.Absent : OfficialProfileFieldState.Present,
                    pictureId);
                NameCardId = new(
                    detail.NameCardId == 0
                        ? OfficialProfileFieldState.Absent
                        : OfficialProfileFieldState.Present,
                    detail.NameCardId);
                break;

            case PlayerSignatureNotify notification:
                Signature = new(
                    string.IsNullOrEmpty(notification.Signature)
                        ? OfficialProfileFieldState.Absent
                        : OfficialProfileFieldState.Present,
                    notification.Signature);
                break;
        }
    }
}
