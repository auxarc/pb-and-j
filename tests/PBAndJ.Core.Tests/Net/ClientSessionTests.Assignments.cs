using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Assignments: which units a client stores as its own -- and, under the same
    // banner the author gave them, the two tests that log a peer joining or
    // leaving.
    // An empty `// --- roster ---` banner headed this section until the commit
    // after the split removed it. A client mirrors the roster the host sends
    // (see .Lobby.cs) but never builds one, so there was nothing for that banner
    // to head; building it is the host's job, tested in HostSessionTests.Roster.cs.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- assignments ---

        [Fact]
        public void HandleMessage_Assignments_StoresOnlyItsOwnUnits()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a", "unit_c" }),
                new PeerAssignment(1, new[] { "unit_b" }),
            }));

            Assert.Equal(new[] { "unit_b" }, client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control: unit_b"));
        }

        [Fact]
        public void HandleMessage_Assignments_WithNoUnitsForUs_SaysSo()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a" }),
            }));

            Assert.Empty(client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control no units"));
        }

        [Fact]
        public void HandleMessage_Assignments_ReplacesPreviousOwnership()
        {
            var client = Welcomed();
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_b" }) }));
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_c" }) }));
            Assert.Equal(new[] { "unit_c" }, client.OwnedUnits);
        }

        [Fact]
        public void OwnedUnits_IsEmptyBeforeAnyAssignment()
        {
            Assert.Empty(Welcomed().OwnedUnits);
        }

        [Fact]
        public void HandleMessage_PeerJoined_Logs()
        {
            Assert.Contains("#2", Single<LogEffect>(Welcomed().HandleMessage(0, new PeerJoinedMessage(2, "ally2"))).Line);
        }

        [Fact]
        public void HandleMessage_PeerLeft_Logs()
        {
            Assert.Contains("peer left: #2", Single<LogEffect>(
                Welcomed().HandleMessage(0, new PeerLeftMessage(2, "ally2"))).Line);
        }
    }
}
