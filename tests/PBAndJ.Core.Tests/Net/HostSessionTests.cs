using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public partial class HostSessionTests
    {
        // The shared fixture. Every helper below is called from MORE THAN ONE part;
        // a helper whose sole user is one part (>=90% of its call sites) lives with
        // that part instead. The other eighteen files are:
        //
        //   .BaseMirror.cs     the overworld base mirror (M12a)
        //   .Construction.cs   constructing a HostSession
        //   .Handshake.cs      who is admitted, who is rejected
        //   .Disconnect.cs     a peer going away
        //   .TurnCycle.cs      the barrier, ownership, completion, un-ready
        //   .Orders.cs         order results
        //   .Reconnect.cs      rejoin, resume tokens, the grace window
        //   .Snapshots.cs      the snapshot broadcast
        //   .Compatibility.cs  build compatibility and the handshake deadline (M7)
        //   .Motion.cs         keyframes (M6) and poses (M8)
        //   .Effects.cs        replayed effects (M14)
        //   .Keepalive.cs      pings, timeouts, liveness
        //   .Combat.cs         combat edges, joining mid-execution, joining mid-combat
        //   .Transport.cs      transport failure and teardown
        //   .Lobby.cs          the lobby (M11a) and who may still join once it seals
        //   .LobbyReady.cs     lobby select / ready / unready mechanics
        //   .Load.cs           the synchronised load (M11d)
        //   .Roster.cs         the roster the screen reads (M11c)
        //
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private HostSession Host(int maxPeers = 3) => new HostSession("host", "7f3a91", maxPeers, bridge, "secret", SessionRequirements.None);

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name, null, null);

        private static OrderPayload Order(string owner) => new OrderPayload("move_run", owner, 0f, 2f);

        /// <summary>Connects and handshakes a peer, discarding the effects.</summary>
        private HostSession WithPeer(int peerId = 1, string name = "ally", int maxPeers = 3)
        {
            var host = Host(maxPeers);
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            host.HandleMessage(peerId, GoodHello(name));
            return host;
        }

        private static IEnumerable<T> Messages<T>(IEnumerable<PbjEffect> effects) where T : PbjMessage =>
            effects.OfType<SendEffect>().Select(e => e.Message).OfType<T>();

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        private static UnitSnapshot Snap(string name) =>
            new UnitSnapshot(name, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f);

        private HostSession Executing()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            return host;
        }

        private static KeyframeCapture Motion() =>
            new KeyframeCapture(15f, 20f, new[]
            {
                new UnitTrack("unit_a", new[]
                {
                    new TransformKey(15f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(20f, new Vec3(9f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            });

        //
        // The lobby only runs out of combat, so these start from a bridge that
        // is not in one. WithPeer() alone would leave the host in Planning.

        private HostSession LobbyHost(int peerId = 1, string name = "ally")
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            host.HandleMessage(peerId, GoodHello(name));
            return host;
        }

        private static LobbyStateMessage LobbyState(IEnumerable<PbjEffect> effects) =>
            All<BroadcastEffect>(effects).Select(e => e.Message).OfType<LobbyStateMessage>().Last();

        private static LobbySelectEventPair Select(string key = "pbj_campaign", string? digest = "abc") =>
            new LobbySelectEventPair(key, digest);

        private sealed class LobbySelectEventPair
        {
            public LobbySelectEventPair(string? key, string? digest)
            {
                Event = new LocalLobbySelectEvent(key, digest);
            }

            public LocalLobbySelectEvent Event { get; }
        }

    }
}
