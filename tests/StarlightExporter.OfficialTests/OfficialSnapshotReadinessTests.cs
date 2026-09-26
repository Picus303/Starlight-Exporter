using Starlight.Protocol;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialSnapshotReadinessTests
{
    [Fact]
    public void RequiresUsablePlayerWeaponBornAvatarAndCurrentTeamAcrossPushes()
    {
        var readiness = new OfficialSnapshotReadiness(loginAccepted: true);
        readiness.Observe(new PlayerDataNotify { NickName = "Traveler" });
        readiness.Observe(new PlayerStoreNotify
        {
            StoreType = (StoreType)1,
            ItemList = { new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } } },
        });
        readiness.Observe(new AvatarDataNotify
        {
            CurAvatarTeamId = 1,
            AvatarList =
            {
                new AvatarInfo { AvatarId = 10000005, Guid = 300, EquipGuidList = { 200 } },
            },
            AvatarTeamMap = { [1] = new AvatarTeam { AvatarGuidList = { 300 } } },
        });
        Assert.False(readiness.IsReady);

        readiness.Observe(new PlayerStoreNotify
        {
            StoreType = (StoreType)1,
            ItemList =
            {
                new Item { ItemId = 11101, Guid = 200, Equip = new Equip { Weapon = new Weapon { Level = 20 } } },
            },
        });
        Assert.True(readiness.SnapshotCoverage);
        Assert.False(readiness.IsReady);
        readiness.MarkCaptureBoundaryValidated();
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void EmptyNotificationsDoNotDeclareSnapshotReady()
    {
        var readiness = new OfficialSnapshotReadiness(loginAccepted: true);
        readiness.Observe(new PlayerDataNotify());
        readiness.Observe(new PlayerStoreNotify { StoreType = (StoreType)1 });
        readiness.Observe(new AvatarDataNotify());

        Assert.False(readiness.IsReady);
    }

    [Fact]
    public void CacheAppliesOrderedReplaceUpsertDeleteAndLazyGuidResolution()
    {
        var cache = new OfficialSnapshotCache();
        var player = new PlayerDataNotify { NickName = "Traveler" };
        player.PropMap[1] = PlayerProperty.Level.Value(10);
        cache.Observe(player);
        cache.Observe(new PlayerPropNotify
        {
            PropMap = { [2] = PlayerProperty.Level.Value(20) },
        });
        Assert.Equal(2, cache.PlayerData!.PropMap.Count);

        var born = new AvatarInfo
        {
            AvatarId = 10000005,
            Guid = 300,
            EquipGuidList = { 200 },
        };
        cache.Observe(new AvatarDataNotify
        {
            CurAvatarTeamId = 1,
            ChooseAvatarGuid = 300,
            AvatarList = { born },
            AvatarTeamMap = { [1] = new AvatarTeam { AvatarGuidList = { 300 } } },
        });
        cache.Observe(new AvatarDataNotify());
        Assert.True(cache.Avatars.ContainsKey(300));
        Assert.False(cache.HasValidInvariants);

        cache.Observe(new PlayerStoreNotify
        {
            StoreType = (StoreType)2,
            ItemList = { new Item { ItemId = 999, Guid = 999, Material = new Material { Count = 1 } } },
        });
        Assert.Empty(cache.PackItems);

        cache.Observe(new PlayerStoreNotify
        {
            StoreType = (StoreType)1,
            ItemList = { WeaponItem(200) },
        });
        Assert.True(cache.HasCoverage);
        Assert.True(cache.HasValidInvariants);

        cache.Observe(new AvatarEquipChangeNotify
        {
            AvatarGuid = 300,
            EquipGuid = 201,
            Weapon = new SceneWeaponInfo(),
        });
        Assert.Contains(201ul, cache.Avatars[300].EquipGuidList);
        Assert.False(cache.HasValidInvariants);
        cache.Observe(new StoreItemChangeNotify
        {
            StoreType = (StoreType)1,
            ItemList = { WeaponItem(201) },
        });
        Assert.DoesNotContain(200ul, cache.Avatars[300].EquipGuidList);
        Assert.Contains(201ul, cache.Avatars[300].EquipGuidList);
        Assert.True(cache.HasValidInvariants);

        cache.Observe(new StoreItemChangeNotify
        {
            StoreType = (StoreType)1,
            ItemList = { new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } } },
        });
        Assert.Equal(3, cache.PackItems.Count);
        cache.Observe(new StoreItemDelNotify
        {
            StoreType = (StoreType)1,
            GuidList = { 100 },
        });
        Assert.Equal(2, cache.PackItems.Count);

        cache.Observe(new AvatarDelNotify { AvatarGuidList = { 300 } });
        Assert.Empty(cache.Avatars);
        Assert.Empty(cache.Teams[1].AvatarGuidList);
        Assert.False(cache.HasValidInvariants);
    }

    private static Item WeaponItem(ulong guid) => new()
    {
        ItemId = 11101,
        Guid = guid,
        Equip = new Equip { Weapon = new Weapon { Level = 20 } },
    };
}
