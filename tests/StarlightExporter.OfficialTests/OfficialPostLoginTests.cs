using Google.Protobuf;
using Starlight.Protocol;
using Starlight.Protocol.V70;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialPostLoginTests
{
    [Fact]
    public void LoginSuccessPlansObservedRequestsForBothTag592Branches()
    {
        var normal = new OfficialPostLoginRequestPlanner(123456789);
        Assert.Collection(normal.OnLoginAccepted(limitedSocialCache: false),
            message => Assert.IsType<GetPlayerFriendListReq>(message),
            message => Assert.IsType<GetPlayerBlacklistReq>(message),
            message => Assert.IsType<GetChatEmojiCollectionReq>(message),
            message => Assert.Equal(900u, Assert.IsType<GetShopReq>(message).ShopType));

        var limited = new OfficialPostLoginRequestPlanner(123456789);
        Assert.Equal(900u,
            Assert.IsType<GetShopReq>(Assert.Single(limited.OnLoginAccepted(true))).ShopType);
        Assert.Throws<InvalidOperationException>(() => limited.OnLoginAccepted(true));
    }

    [Fact]
    public void PlayerDataRequestsOwnSocialDetailOnlyOnceOnV70UidTag()
    {
        var planner = new OfficialPostLoginRequestPlanner(123456789);
        GetPlayerSocialDetailReq request = Assert.IsType<GetPlayerSocialDetailReq>(planner.OnPlayerData());
        Assert.Equal(123456789u, request.Uid);
        Assert.Null(planner.OnPlayerData());

        byte[] body = new V70ProtocolRegistry().Serialize(request);
        using var input = new CodedInputStream(body);
        Assert.Equal(120u, input.ReadTag());
        Assert.Equal(123456789u, input.ReadUInt32());
        Assert.Equal(0u, input.ReadTag());
    }

    [Fact]
    public void SocialValuesRemainUnknownUntilMatchingSuccessfulDetailThenTrackAbsenceAndUpdates()
    {
        var profile = new OfficialSocialProfileTracker(123456789);
        profile.Observe(new GetPlayerSocialDetailRsp
        {
            DetailData = new SocialDetail { Uid = 999, Signature = "other-player" },
        });
        Assert.Equal(OfficialProfileFieldState.Unknown, profile.Signature.State);
        Assert.False(profile.DetailAccepted);

        profile.Observe(new GetPlayerSocialDetailRsp
        {
            DetailData = new SocialDetail
            {
                Uid = 123456789,
                Signature = "synthetic-signature",
                NameCardId = 210001,
            },
        });
        Assert.True(profile.DetailAccepted);
        Assert.Equal(OfficialProfileFieldState.Present, profile.Signature.State);
        Assert.Equal("synthetic-signature", profile.Signature.Value);
        Assert.Equal(OfficialProfileFieldState.Absent, profile.PictureId.State);
        Assert.Equal(OfficialProfileFieldState.Present, profile.NameCardId.State);
        Assert.Equal(210001u, profile.NameCardId.Value);

        profile.Observe(new PlayerSignatureNotify { Signature = string.Empty });
        Assert.Equal(OfficialProfileFieldState.Absent, profile.Signature.State);
        Assert.Equal(string.Empty, profile.Signature.Value);
    }

    [Fact]
    public void SceneReadyReleasesInitAndDoneButNeverPostsAutomatically()
    {
        var planner = new OfficialSceneProgressionPlanner();

        EnterSceneReadyReq ready = Assert.IsType<EnterSceneReadyReq>(Assert.Single(
            planner.Observe(new PlayerEnterSceneNotify { EnterSceneToken = 123 })));
        Assert.Equal(123u, ready.EnterSceneToken);
        IReadOnlyList<Starlight.Protobuf.Core.IMessage> released = planner.Observe(
            new EnterSceneReadyRsp { EnterSceneToken = 123, Retcode = 0 });
        Assert.Collection(released,
            message => Assert.Equal(123u,
                Assert.IsType<SceneInitFinishReq>(message).EnterSceneToken),
            message => Assert.Equal(123u,
                Assert.IsType<EnterSceneDoneReq>(message).EnterSceneToken));

        Assert.Empty(planner.Observe(
            new SceneInitFinishRsp { EnterSceneToken = 123, Retcode = 0 }));
        Assert.Empty(planner.Observe(
            new EnterSceneDoneRsp { EnterSceneToken = 123, Retcode = 0 }));
        PostEnterSceneReq post = planner.CreatePostRequest(12.5);
        Assert.Equal(123u, post.EnterSceneToken);
        Assert.Equal(12.5, post.TotalTickTime);
    }
}
