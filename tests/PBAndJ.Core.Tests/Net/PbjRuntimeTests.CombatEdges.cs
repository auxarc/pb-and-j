using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The combat edge -- the transition the runtime watches for rather than is told
    // about. When it fires, when it deliberately does not, and why it is seeded at
    // construction so a runtime built mid-fight does not announce a start that has
    // already happened.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
        // --- combat edges ---

        [Fact]
        public void Pump_WhenCombatStarts_SaysNothingUntilTheFightIsShipped()
        {
            // M12b: CombatStart is what makes a client refuse scenario offers, so
            // sending it at the edge would stop anyone fetching the fight it is
            // announcing. The edge is now silent; the offer comes first.
            bridge.InCombat = false;
            var runtime = WithHandshakenPeer();

            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            runtime.Pump(0);

            Assert.DoesNotContain(transport.MessagesTo(1), m => m is CombatStartMessage);
        }

        [Fact]
        public void Pump_TheHostEnteringCombat_AsksTheBridgeToShipTheFight()
        {
            // The whole point of routing this through an effect rather than
            // letting the glue watch InCombat for itself: the ask lands inside
            // the pump that observed the edge, so the glue can never start
            // polling before the session knows a fight is on.
            // Out of combat FIRST: the fake bridge starts in combat, so a runtime
            // built without this observes the edge during the handshake pump and
            // the ask has already happened before the test does anything.
            bridge.InCombat = false;
            var runtime = WithHandshakenPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            runtime.Pump(1);

            Assert.Equal(1, bridge.ShipCombatCalls);
        }

        [Fact]
        public void Pump_WhenTheFightIsShipped_OffersItToPeers()
        {
            bridge.InCombat = false;
            var runtime = WithHandshakenPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            runtime.Pump(0);

            runtime.Post(new LocalCombatReadyEvent("pbj_combat_test", "d1"));
            runtime.Pump(1);

            Assert.Contains(transport.MessagesTo(1), m => m is CombatOfferMessage);
        }

        [Fact]
        public void Pump_WhenCombatEnds_AnnouncesItToPeers()
        {
            var runtime = WithHandshakenPeer();

            bridge.InCombat = false;
            runtime.Pump(0);

            Assert.Contains(transport.MessagesTo(1), m => m is CombatEndMessage);
        }

        [Fact]
        public void Pump_WithNoChangeInCombatState_AnnouncesNothing()
        {
            var runtime = WithHandshakenPeer();
            runtime.Pump(0);
            runtime.Pump(0);

            // The handshake's own CombatStart is expected and does not repeat;
            // pumping a state that has not changed must announce nothing further.
            Assert.Single(transport.MessagesTo(1), m => m is CombatStartMessage);
            Assert.DoesNotContain(transport.MessagesTo(1), m => m is CombatEndMessage);
        }

        [Fact]
        public void Constructor_SeedsTheCombatEdgeSoAMidCombatStartDoesNotFireOne()
        {
            // bridge.InCombat is already true when the session is constructed.
            var runtime = WithHandshakenPeer();
            runtime.Pump(0);

            // Exactly one, and it is the handshake's: a peer joining mid-combat
            // is told so on purpose (M11d). What must not happen is a SECOND one
            // from a combat-entered edge that never actually occurred.
            Assert.Single(transport.MessagesTo(1), m => m is CombatStartMessage);
        }

        [Fact]
        public void Pump_ObservesTheCombatEdgeAfterDraining_SoAFinalTurnsResultsStillGoOut()
        {
            // The last turn's execution is what ended the combat: the queued
            // LocalTurnComplete and the cleared InCombat flag arrive together.
            // Observing the edge first would flip the host to Lobby and the
            // TurnComplete would be swallowed by its State != Executing guard.
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);
            Assert.Equal(HostSessionState.Executing, host.State);

            bridge.InCombat = false;
            mailbox.Post(new LocalTurnCompleteEvent("deadbeef", null, null));
            runtime.Pump(0);

            var sent = transport.MessagesTo(1);
            Assert.Contains(sent, m => m is TurnCompleteMessage);
            Assert.Contains(sent, m => m is CombatEndMessage);
        }
    }
}
