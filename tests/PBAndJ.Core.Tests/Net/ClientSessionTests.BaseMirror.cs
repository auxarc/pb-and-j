using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The base mirror (M12a): what a client does with the host's base position.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- the base mirror (M12a) ---

        [Fact]
        public void BasePosition_BecomesAMirrorEffect()
        {
            var client = Welcomed();

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1024.5f, -37.25f));

            var mirror = Single<MirrorBaseEffect>(effects);
            Assert.Equal(1024.5f, mirror.X);
            Assert.Equal(-37.25f, mirror.Z);
        }

        [Fact]
        public void BasePosition_IsMirroredWhateverTheSessionState()
        {
            // No ClientSessionState guard on purpose, and this is the trap it
            // avoids: HandleWelcome seeds that state from this machine's OWN
            // combat flag, so a peer who joined while their local game was
            // mid-fight lands in a state that says nothing true about the host.
            // Gating the mirror on it would freeze that player's map for the
            // session. The mirror is presentation and cannot desynchronise
            // anything, so it needs no such permission.
            var lobby = Welcomed();
            var fighting = Welcomed();
            fighting.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(1));

            Assert.Single(All<MirrorBaseEffect>(lobby.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f))));
            Assert.Single(All<MirrorBaseEffect>(fighting.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f))));
        }

        [Fact]
        public void BasePosition_BeforeTheHandshake_IsAProtocolViolationLikeAnythingElse()
        {
            // Not an exception for the mirror. The host controls ordering, so a
            // position arriving before Welcome means the far side is not the
            // protocol we think it is -- and the existing guard is the whole
            // reason a client can trust anything it is later told.
            var client = Client();
            client.Start();

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f));

            Assert.Empty(All<MirrorBaseEffect>(effects));
            Assert.Single(All<DisconnectEffect>(effects));
        }
    }
}
