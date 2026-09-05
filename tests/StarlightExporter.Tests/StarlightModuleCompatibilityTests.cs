using Google.Protobuf;
using Starlight.Game.Player;
using Starlight.Game.Resources;
using Starlight.Protocol;
using Starlight.Rpc.Proto;
using StarlightExporter.Mapping;
using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class StarlightModuleCompatibilityTests
{
    [Fact]
    public async Task ApplicationValidatorAcceptsMappedStateWithoutRepair()
    {
        GameData data = TestGameData.Create();
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-valid.json"));
        StarlightMappingResult mapping = new StarlightSnapshotMapper(data).Map(snapshot);

        StarlightModuleValidationResult result = await StarlightModuleCompatibilityValidator.ValidateAsync(
            snapshot.Manifest.OfficialUid,
            data,
            mapping.Profile,
            mapping.State);

        Assert.True(result.IsCompatible, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(result.StatePreserved);
        Assert.True(result.StoreNotificationMatches);
        Assert.True(result.AvatarNotificationMatches);
        Assert.Empty(result.RepairNotifications);
        Assert.Equal(new ModuleValidationCounts(1, 1, 1, 4), result.Loaded);
    }

    [Fact]
    public async Task ApplicationValidatorDetectsStarlightWeaponRepair()
    {
        NetPlayerState incomplete = CreateKnownGoodState();
        incomplete.Weapons.Clear();

        StarlightModuleValidationResult result = await StarlightModuleCompatibilityValidator.ValidateAsync(
            playerUid: 765432100,
            TestGameData.Create(),
            new NetPlayerProfile { Nickname = "Traveler" },
            incomplete);

        Assert.False(result.IsCompatible);
        Assert.False(result.StatePreserved);
        Assert.Contains("StoreItemChangeNotify", result.RepairNotifications);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MODULE_STATE_MUTATED");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MODULE_REPAIR_NOTIFICATION");
    }

    [Fact]
    public async Task ApplicationValidatorDetectsSilentlyDroppedInventoryItem()
    {
        NetPlayerState incomplete = CreateKnownGoodState();
        incomplete.Materials.Add(new NetMaterial { ItemId = 99999, Guid = 99999, Count = 1 });

        StarlightModuleValidationResult result = await StarlightModuleCompatibilityValidator.ValidateAsync(
            playerUid: 765432100,
            TestGameData.Create(),
            new NetPlayerProfile { Nickname = "Traveler" },
            incomplete);

        Assert.False(result.IsCompatible);
        Assert.True(result.StatePreserved);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MODULE_ENTITY_COUNT_MISMATCH");
    }

    [Fact]
    public async Task MinimalSnapshotMapsAndLoadsWithoutStarlightRepair()
    {
        GameData data = TestGameData.Create();
        data.MaterialData[1001].StackLimit = 5;
        data.WeaponData[11101].GadgetId = 50099999;
        data.WeaponData[11101].SkillAffix = [11999];
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-valid.json"));
        var mapper = new StarlightSnapshotMapper(data);

        StarlightMappingResult mapping = mapper.Map(snapshot);

        Assert.True(
            mapping.IsSuccess,
            string.Join(Environment.NewLine, mapping.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        Assert.Equal("Traveler", mapping.Profile.Nickname);
        Assert.Equal(expected: 5u, Assert.Single(mapping.State.Materials).Count);
        NetWeapon mappedWeapon = Assert.Single(mapping.State.Weapons);
        Assert.Equal(expected: 50099999u, mappedWeapon.GadgetId);
        Assert.Equal(expected: 11999u, mappedWeapon.AffixId);
        Assert.Contains(mapping.Issues, issue => issue.Code == "STACK_CLAMPED");
        Assert.Contains(mapping.Issues, issue => issue.Code == "TEAM_SLOTS_COMPLETED");
        Assert.Equal(expected: 4, mapping.State.AvatarTeams.Count);

        byte[] stateBeforeLogin = mapping.State.ToByteArray();
        var (player, sent) = TestStarlightPlayer.Create(data, mapping.State, mapping.Profile);

        await player.Module<InventoryModule>().OnLogin();
        AvatarDataNotify avatarNotify = await player.Module<AvatarModule>().OnLogin();
        player.Module<TeamModule>().OnLogin();

        Assert.Equal(stateBeforeLogin, player.State.ToByteArray());
        Assert.Single(player.Module<InventoryModule>().Materials);
        Assert.Single(player.Module<InventoryModule>().Weapons);
        Assert.Single(player.Module<AvatarModule>().Avatars);
        Assert.Equal(expected: 4, player.Module<TeamModule>().Teams.Count);
        Assert.Equal(expected: 1u, avatarNotify.CurAvatarTeamId);
        Assert.Equal(expected: 30001ul, avatarNotify.ChooseAvatarGuid);
        Assert.DoesNotContain(sent, message => message is AvatarEquipChangeNotify);
    }

    [Fact]
    public async Task PinnedStarlightLoadsKnownGoodStateWithoutRepair()
    {
        GameData data = TestGameData.Create();
        NetPlayerState state = CreateKnownGoodState();
        byte[] stateBeforeLogin = state.ToByteArray();
        var (player, sent) = TestStarlightPlayer.Create(data, state);

        InventoryModule inventory = player.Module<InventoryModule>();
        AvatarModule avatars = player.Module<AvatarModule>();
        TeamModule teams = player.Module<TeamModule>();

        await inventory.OnLogin();
        AvatarDataNotify avatarNotify = await avatars.OnLogin();
        teams.OnLogin();

        MaterialItem material = Assert.Single(inventory.Materials);
        Assert.Equal(expected: 1001u, material.ItemId);
        Assert.Equal(expected: 10001ul, material.Guid);
        Assert.Equal(expected: 10u, material.Count);

        WeaponItem weapon = Assert.Single(inventory.Weapons);
        Assert.Equal(expected: 11101u, weapon.ItemId);
        Assert.Equal(expected: 20001ul, weapon.Guid);
        Assert.Equal(expected: 20u, weapon.Level);
        Assert.Equal(expected: 1u, weapon.Refinement);
        Assert.Equal(expected: 1u, weapon.PromoteLevel);
        Assert.Equal(expected: 11101u, weapon.AffixId);
        Assert.Equal(expected: 50011101u, weapon.GadgetId);

        Avatar avatar = Assert.Single(avatars.Avatars.Values);
        Assert.Equal(expected: 10000005u, avatar.AvatarId);
        Assert.Equal(expected: 30001ul, avatar.Guid);
        Assert.Equal(weapon.Guid, avatar.WeaponGuid);

        PlayerTeam current = teams.Current;
        Assert.Equal(expected: 1u, current.Id);
        Assert.Equal(avatar.Guid, current.CurrentAvatarGuid);
        Assert.Equal(expected: 4, teams.Teams.Count);

        Assert.Equal(current.Id, avatarNotify.CurAvatarTeamId);
        Assert.Equal(current.CurrentAvatarGuid, avatarNotify.ChooseAvatarGuid);
        Assert.Equal(expected: 4, avatarNotify.AvatarTeamMap.Count);
        Assert.DoesNotContain(sent, message => message is AvatarEquipChangeNotify);
        Assert.Equal(stateBeforeLogin, player.State.ToByteArray());

        NetPlayerState reparsed = NetPlayerState.Parser.ParseFrom(player.State.ToByteArray());
        Assert.Single(reparsed.Materials);
        Assert.Single(reparsed.Weapons);
        Assert.Single(reparsed.Avatars);
        Assert.Equal(expected: 4, reparsed.AvatarTeams.Count);
    }

    private static NetPlayerState CreateKnownGoodState()
    {
        var state = new NetPlayerState
        {
            BornState = NetPlayerState.Types.PlayerBornState.Complete,
            BornAvatarId = 10000005,
            CurrentAvatarTeamId = 1,
            Materials = {
                new NetMaterial { ItemId = 1001, Guid = 10001, Count = 10 }
            },
            Weapons = {
                new NetWeapon {
                    ItemId = 11101,
                    Guid = 20001,
                    Level = 20,
                    Refinement = 1,
                    PromoteLevel = 1,
                    AffixId = 11101,
                    GadgetId = 50011101
                }
            },
            Avatars = {
                new NetAvatar {
                    AvatarId = 10000005,
                    Guid = 30001,
                    Level = 20,
                    Constellation = 0,
                    BornTime = 1788516000,
                    WeaponGuid = 20001
                }
            }
        };

        state.AvatarTeams.Add(CreateTeam(teamId: 1, avatarGuid: 30001));
        state.AvatarTeams.Add(CreateTeam(teamId: 2));
        state.AvatarTeams.Add(CreateTeam(teamId: 3));
        state.AvatarTeams.Add(CreateTeam(teamId: 4));
        return state;
    }

    private static NetAvatarTeam CreateTeam(uint teamId, ulong? avatarGuid = null)
    {
        var team = new NetAvatarTeam
        {
            TeamId = teamId,
            Name = $"Team {teamId}",
            CurrentAvatarGuid = avatarGuid ?? 0
        };

        if (avatarGuid is not null)
        {
            team.AvatarGuids.Add(avatarGuid.Value);
        }

        return team;
    }

}
