using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The overworld base mirror (M12a): the host's base position going out to everyone.
    // One section of the original, moved whole. No helper of its own.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- the base mirror (M12a) ---

        [Fact]
        public void LocalBasePosition_IsBroadcastToEveryone()
        {
            var host = WithPeer();

            var effects = host.Handle(new LocalBasePositionEvent(1024.5f, -37.25f));

            var message = Assert.IsType<BasePositionMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.Equal(1024.5f, message.X);
            Assert.Equal(-37.25f, message.Z);
        }

        [Fact]
        public void LocalBasePosition_WithNobodyListening_StillBroadcasts()
        {
            // A broadcast to an empty registry is already nothing, so guarding on
            // the peer count here would be a branch with no observable difference
            // -- and the 100% gate turns an indistinguishable branch into a build
            // failure rather than dead code.
            var effects = Host().Handle(new LocalBasePositionEvent(1f, 2f));

            Assert.Single(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void LocalBasePosition_RepeatedWithTheSameValue_IsSentEveryTime()
        {
            // The heartbeat's whole purpose is to repeat itself while nothing
            // moves. A session that suppressed identical updates would be
            // deciding cadence on the glue's behalf, and would silently turn the
            // heartbeat back into movement-only updates.
            var host = WithPeer();

            host.Handle(new LocalBasePositionEvent(5f, 5f));
            var second = host.Handle(new LocalBasePositionEvent(5f, 5f));

            Assert.Single(All<BroadcastEffect>(second));
        }

        [Fact]
        public void LocalBasePosition_DuringCombat_IsStillBroadcast()
        {
            // The base has a position in every state, and a client that stopped
            // hearing about it while the host was busy would simply be wrong
            // afterwards.
            var host = WithPeer();
            host.Handle(new CombatEnteredEvent());

            Assert.Single(All<BroadcastEffect>(host.Handle(new LocalBasePositionEvent(3f, 4f))));
        }
    }
}
