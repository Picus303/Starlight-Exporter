using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

// Produces only requests observed on the Windows Global 7.0.0 login path.
// The login branch flag is V70 PlayerLoginRsp tag 592, read from the wire.
public sealed class OfficialPostLoginRequestPlanner(uint playerUid)
{
    private bool _loginHandled;
    private bool _socialRequested;

    public IReadOnlyList<IMessage> OnLoginAccepted(bool limitedSocialCache)
    {
        if (_loginHandled)
        {
            throw new InvalidOperationException("The post-login requests were already planned.");
        }
        _loginHandled = true;

        if (limitedSocialCache)
        {
            return [new GetShopReq { ShopType = 900 }];
        }
        return
        [
            new GetPlayerFriendListReq(),
            new GetPlayerBlacklistReq(),
            new GetChatEmojiCollectionReq(),
            new GetShopReq { ShopType = 900 },
        ];
    }

    public GetPlayerSocialDetailReq? OnPlayerData()
    {
        if (_socialRequested)
        {
            return null;
        }
        if (playerUid == 0)
        {
            throw new InvalidOperationException("A player UID is required for social detail.");
        }
        _socialRequested = true;
        return new GetPlayerSocialDetailReq { Uid = playerUid };
    }
}
