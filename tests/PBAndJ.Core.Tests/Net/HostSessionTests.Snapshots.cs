using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The snapshot broadcast. One section of the original, minus its two helpers.
    //
    // Snap and Executing were declared under this banner but are not this section's:
    // Snap is called 11 times from .Effects.cs and 10 from .Motion.cs against 2 here,
    // and Executing is called 3 times here against 11 each from .Effects.cs and
    // .Motion.cs, and once from .Combat.cs. Both are shared
    // fixture in the primary now.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- snapshots ---

        [Fact]
        public void TurnComplete_BroadcastsTheSnapshotAfterTurnComplete()
        {
            // Snapshot-first would make the client's digest already match when it
            // compared, silencing the divergence diagnostic permanently.
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, null))
                .ToList();

            var completeAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is TurnCompleteMessage);
            var snapshotAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is SnapshotMessage);
            Assert.True(completeAt >= 0 && snapshotAt > completeAt);
        }

        [Fact]
        public void TurnComplete_SnapshotCarriesTheExecutedTurnAndTheSameDigest()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, null));
            var snapshot = (SnapshotMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is SnapshotMessage).Message;

            // The executed turn, captured at commit time — not read back from the
            // bridge, which has already advanced.
            Assert.Equal(3, snapshot.Turn);
            Assert.Equal("abc", snapshot.Digest);
            Assert.Equal("unit_a", Assert.Single(snapshot.Units).Name);
        }

        [Fact]
        public void TurnComplete_WithNoUnits_StillBroadcastsASnapshot()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", null, null));
            var snapshot = (SnapshotMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is SnapshotMessage).Message;
            Assert.Empty(snapshot.Units);
        }
    }
}
