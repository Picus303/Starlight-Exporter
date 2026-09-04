using Microsoft.Extensions.DependencyInjection;
using Starlight.Common;
using Starlight.Game.Modules;
using Starlight.Game.Player;
using Starlight.Game.Resources;
using Starlight.Rpc.Proto;
using Starlight.Rpc.Tunnel;
using IMessage = Starlight.Protobuf.Core.IMessage;

namespace StarlightExporter.Tests;

internal static class TestStarlightPlayer
{
    public static (StarlightPlayer Player, List<IMessage> Sent) Create(
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
        var player = new StarlightPlayer(services, registry, tunnel) {
            Uid = 765432100,
            State = state,
            Profile = profile ?? new NetPlayerProfile {
                Nickname = "Traveler",
                Signature = "Sanitized fixture",
                PictureId = 10000005,
                NameCardId = 210001
            }
        };

        return (player, tunnel.Sent);
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
