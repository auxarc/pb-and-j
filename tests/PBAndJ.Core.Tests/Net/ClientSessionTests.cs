using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public partial class ClientSessionTests
    {
        // The shared fixture. Each of the six helpers below is called from more
        // than one part -- checked, not assumed: the least-shared of them,
        // Welcome, is called from this file and from .Construction.cs, and the
        // most-shared, Welcomed, from thirteen parts.
        //
        // A helper whose callers all land in ONE part lives with that part
        // instead. None had to be moved here to satisfy that: where a helper
        // spanned two neighbouring sections, the sections were grouped into one
        // part rather than the helper hoisted -- see .Playback.cs and .Lobby.cs,
        // which say so in their own headers.
        //
        // The eleven other parts, in the order the original file had them:
        //
        //   .BaseMirror.cs    the base mirror (M12a)
        //   .CombatEntry.cs   joining the host's fight (M12b)
        //   .Construction.cs  construction and the handshake
        //   .Assignments.cs   which units this client owns
        //   .TurnCycle.cs     readying, the owned-units filter, un-readying
        //   .Combat.cs        the combat lifecycle, and order results
        //   .Reconnect.cs     reconnecting, and snapshot correction
        //   .Playback.cs      keyframes (M6), poses (M8), effects (M14)
        //   .Keepalive.cs     pings, timeouts, losing the host
        //   .Plumbing.cs      the edges that belong to no feature
        //   .Lobby.cs         the lobby (M11a), its counts (M11c), the load (M11d)
        //
        // The boundaries are the author's own `// --- name ---` banners, of which
        // this file had twenty-one. No section was divided: none of them reaches
        // the 500-line gate on its own, so this split is entirely a question of
        // which neighbours to group.
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private ClientSession Client() => new ClientSession("ally", "0.2.0", bridge);

        private static WelcomeMessage Welcome(int turn = 3) =>
            new WelcomeMessage(PbjProtocol.Version, "7f3a91", 1, "host",
                new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") }, turn, "tok");

        /// <summary>A client that has completed the handshake.</summary>
        private ClientSession Welcomed(int turn = 3)
        {
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, Welcome(turn));
            return client;
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

    }
}
