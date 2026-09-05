using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Starlight.Common;
using Starlight.Game.Modules;
using Starlight.Game.Player;
using Starlight.Game.Resources;
using Starlight.Protocol;
using Starlight.Rpc.Proto;
using Starlight.Rpc.Tunnel;
using IMessage = Starlight.Protobuf.Core.IMessage;

namespace StarlightExporter.Mapping;

public sealed record ModuleValidationCounts(
    int Materials,
    int Weapons,
    int Avatars,
    int Teams);

public sealed record ModuleValidationDiagnostic(string Code, string Message);

public sealed record StarlightModuleValidationResult(
    bool IsCompatible,
    bool StatePreserved,
    bool StoreNotificationMatches,
    bool AvatarNotificationMatches,
    ModuleValidationCounts Loaded,
    IReadOnlyList<string> RepairNotifications,
    IReadOnlyList<ModuleValidationDiagnostic> Diagnostics);

public static class StarlightModuleCompatibilityValidator
{
    public static async Task<StarlightModuleValidationResult> ValidateAsync(
        uint playerUid,
        GameData gameData,
        NetPlayerProfile profile,
        NetPlayerState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(playerUid);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        NetPlayerState workingState = NetPlayerState.Parser.ParseFrom(state.ToByteArray());
        NetPlayerProfile workingProfile = NetPlayerProfile.Parser.ParseFrom(profile.ToByteArray());
        byte[] expectedState = workingState.ToByteArray();
        var diagnostics = new List<ModuleValidationDiagnostic>();

        try
        {
            using ServiceProvider services = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            var registry = new ModuleRegistry();
            var guidManager = new GuidManager(serverId: 1);
            registry.AddModule<InventoryModule>((_, player) => new InventoryModule(player, guidManager, gameData));
            registry.AddModule<AvatarModule>((_, player) => new AvatarModule(player, gameData, guidManager));
            registry.AddModule<TeamModule>((_, player) => new TeamModule(player));
            registry.Build();

            using var tunnel = new RecordingTunnel();
            var player = new StarlightPlayer(services, registry, tunnel) {
                Uid = playerUid,
                State = workingState,
                Profile = workingProfile
            };

            InventoryModule inventory = player.Module<InventoryModule>();
            AvatarModule avatars = player.Module<AvatarModule>();
            TeamModule teams = player.Module<TeamModule>();

            await inventory.OnLogin();
            cancellationToken.ThrowIfCancellationRequested();
            AvatarDataNotify avatarNotification = await avatars.OnLogin();
            cancellationToken.ThrowIfCancellationRequested();
            teams.OnLogin();

            bool statePreserved = expectedState.SequenceEqual(player.State.ToByteArray());
            if (!statePreserved)
            {
                diagnostics.Add(new ModuleValidationDiagnostic(
                    "MODULE_STATE_MUTATED",
                    "The pinned Starlight modules changed the mapped player state during login."));
            }

            var loaded = new ModuleValidationCounts(
                inventory.Materials.Count,
                inventory.Weapons.Count,
                avatars.Avatars.Count,
                teams.Teams.Count);
            bool countsMatch = loaded == new ModuleValidationCounts(
                state.Materials.Count,
                state.Weapons.Count,
                state.Avatars.Count,
                state.AvatarTeams.Count);
            if (!countsMatch)
            {
                diagnostics.Add(new ModuleValidationDiagnostic(
                    "MODULE_ENTITY_COUNT_MISMATCH",
                    "The pinned Starlight modules did not load every mapped entity."));
            }

            PlayerStoreNotify? storeNotification = tunnel.Sent.OfType<PlayerStoreNotify>().SingleOrDefault();
            bool storeNotificationMatches = storeNotification is not null
                && StoreNotificationMatches(storeNotification, inventory);
            if (!storeNotificationMatches)
            {
                diagnostics.Add(new ModuleValidationDiagnostic(
                    "PLAYER_STORE_NOTIFY_MISMATCH",
                    "PlayerStoreNotify does not exactly describe the inventory loaded by Starlight."));
            }

            bool avatarNotificationMatches = AvatarNotificationMatches(avatarNotification, avatars, teams);
            if (!avatarNotificationMatches)
            {
                diagnostics.Add(new ModuleValidationDiagnostic(
                    "AVATAR_DATA_NOTIFY_MISMATCH",
                    "AvatarDataNotify does not exactly describe the avatars and teams loaded by Starlight."));
            }

            string[] repairNotifications = tunnel.Sent
                .Where(message => message is not StoreWeightLimitNotify and not PlayerStoreNotify)
                .Select(message => message.GetType().Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (repairNotifications.Length > 0)
            {
                diagnostics.Add(new ModuleValidationDiagnostic(
                    "MODULE_REPAIR_NOTIFICATION",
                    $"Starlight emitted repair notification(s): {string.Join(", ", repairNotifications)}."));
            }

            return new StarlightModuleValidationResult(
                IsCompatible: diagnostics.Count == 0,
                statePreserved,
                storeNotificationMatches,
                avatarNotificationMatches,
                loaded,
                repairNotifications,
                diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add(new ModuleValidationDiagnostic(
                "MODULE_VALIDATION_FAILED",
                $"Module validation failed with {exception.GetType().Name}."));
            return new StarlightModuleValidationResult(
                IsCompatible: false,
                StatePreserved: false,
                StoreNotificationMatches: false,
                AvatarNotificationMatches: false,
                new ModuleValidationCounts(0, 0, 0, 0),
                [],
                diagnostics);
        }
    }

    private static bool StoreNotificationMatches(PlayerStoreNotify notification, InventoryModule inventory)
    {
        var expected = new PlayerStoreNotify {
            StoreType = StoreType.STORE_TYPE_PACK,
            WeightLimit = 30000,
            ItemList = [
                .. inventory.Materials.Select(item => item.ToProtocol()),
                .. inventory.Weapons.Select(item => item.ToProtocol())
            ]
        };

        return ProtocolMessagesMatch(expected, notification);
    }

    private static bool AvatarNotificationMatches(
        AvatarDataNotify notification,
        AvatarModule avatars,
        TeamModule teams)
    {
        IReadOnlyDictionary<uint, Avatar> loadedAvatars = avatars.Avatars;
        IReadOnlyDictionary<uint, PlayerTeam> loadedTeams = teams.Teams;

        PlayerTeam? current = loadedTeams.GetValueOrDefault(notification.CurAvatarTeamId);
        if (current is null)
        {
            return false;
        }

        var expected = new AvatarDataNotify {
            CurAvatarTeamId = current.Id,
            ChooseAvatarGuid = current.CurrentAvatarGuid,
            OwnedFlycloakList = [Avatar.DefaultFlycloak],
            AvatarList = [.. loadedAvatars.Values.Select(avatar => avatar.Info())]
        };
        foreach ((uint id, PlayerTeam team) in loadedTeams)
        {
            expected.AvatarTeamMap.Add(id, team.Info());
        }

        return ProtocolMessagesMatch(expected, notification);
    }

    private static bool ProtocolMessagesMatch<T>(T expected, T actual) =>
        JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual);

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
