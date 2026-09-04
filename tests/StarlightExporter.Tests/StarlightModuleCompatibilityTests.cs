using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Starlight.Common;
using Starlight.Game.Modules;
using Starlight.Game.Player;
using Starlight.Game.Resources;
using Starlight.Game.Resources.Binary;
using Starlight.Game.Resources.Excel;
using Starlight.Protocol;
using Starlight.Rpc.Proto;
using Starlight.Rpc.Tunnel;
using StarlightExporter.Mapping;
using StarlightExporter.Snapshot;
using Xunit;
using IMessage = Starlight.Protobuf.Core.IMessage;

namespace StarlightExporter.Tests;

public sealed class StarlightModuleCompatibilityTests
{
    [Fact]
    public async Task MinimalSnapshotMapsAndLoadsWithoutStarlightRepair()
    {
        GameData data = CreateGameData();
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
        var (player, sent) = CreatePlayer(data, mapping.State, mapping.Profile);

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
        GameData data = CreateGameData();
        NetPlayerState state = CreateKnownGoodState();
        byte[] stateBeforeLogin = state.ToByteArray();
        var (player, sent) = CreatePlayer(data, state);

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

    private static (StarlightPlayer Player, List<IMessage> Sent) CreatePlayer(
        GameData data,
        NetPlayerState state,
        NetPlayerProfile? profile = null)
    {
        ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var registry = new ModuleRegistry();
        var guidManager = new GuidManager(serverId: 1);

        registry.AddModule<InventoryModule>((_, player) => new InventoryModule(player, guidManager, data));
        registry.AddModule<AvatarModule>((_, player) => new AvatarModule(player, data, guidManager));
        registry.AddModule<TeamModule>((_, player) => new TeamModule(player));
        registry.Build();

        var tunnel = new RecordingTunnel();

        var player = new StarlightPlayer(services, registry, tunnel)
        {
            Uid = 765432100,
            State = state,
            Profile = profile ?? new NetPlayerProfile
            {
                Nickname = "Traveler",
                Signature = "Sanitized fixture",
                PictureId = 10000005,
                NameCardId = 210001
            }
        };

        return (player, tunnel.Sent);
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

    private static GameData CreateGameData()
    {
        var data = new GameData(new ConfigurationBuilder().Build());
        data.MaterialData[1001] = new MaterialData { Id = 1001, StackLimit = 9999 };
        data.WeaponData[11101] = new WeaponData
        {
            Id = 11101,
            GadgetId = 50011101,
            SkillAffix = [11101]
        };
        data.AvatarData[10000005] = new AvatarData
        {
            Id = 10000005,
            InitialWeapon = 11101,
            SkillDepotId = 500,
            HpBase = 100,
            AttackBase = 20,
            DefenseBase = 10,
            CritChanceBase = 0.05f,
            CritDamageBase = 0.5f
        };
        data.AvatarSkillDepotData[500] = new AvatarSkillDepotData
        {
            Id = 500,
            Skills = [501],
            EnergySkill = 502
        };
        data.Avatars[10000005] = new AvatarConfig();
        return data;
    }

    private sealed class RecordingTunnel : RpcTunnel
    {
        public List<IMessage> Sent { get; } = [];

        protected override TunnelMessage Serialize(IMessage message) => new RecordingMessage(message);

        public override IDisposable Subscribe(int id, AsyncTunnelHandler handler) => NoopDisposable.Instance;

        public override IDisposable Subscribe(string id, AsyncTunnelHandler handler) => NoopDisposable.Instance;

        public override Task Publish(int id, TunnelMessage message) => Record(message);

        public override Task Publish(string id, TunnelMessage message) => Record(message);

        protected override void NotifyPeerClosed()
        {
        }

        private Task Record(TunnelMessage message)
        {
            Sent.Add(message.Decode<IMessage>());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMessage(IMessage message) : TunnelMessage
    {
        public override T? TryDecode<T>() where T : class => message as T;

        public override IMessage? TryDecode(Type type) =>
            type.IsInstanceOfType(message) ? message : null;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
