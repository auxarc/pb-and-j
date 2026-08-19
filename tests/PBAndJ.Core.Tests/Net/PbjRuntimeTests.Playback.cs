using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // What a client does with what the host sends -- correcting to a snapshot,
    // presenting keyframes, placing a base, entering and leaving combat -- and the
    // digest property that correction rests on.
    //
    // ClientRuntime and Snap live here rather than in the shared fixture because
    // every one of their call sites is in this file.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
        // --- snapshot correction ---

        private PbjRuntime ClientRuntime(out ClientSession session)
        {
            session = new ClientSession("ally", "0.2.0", bridge);
            var runtime = new PbjRuntime(transport, bridge, log, mailbox, session);
            mailbox.Post(new PeerConnectedEvent(ClientSession.HostConnectionId, "127.0.0.1:1"));
            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, Frame(new WelcomeMessage(
                PbjProtocol.Version, "s", 1, "host", new[] { new PeerInfo(0, "host") }, 3, "tok"))));
            runtime.Pump(0);
            return runtime;
        }

        private static UnitSnapshot Snap(string name) =>
            new UnitSnapshot(name, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f);

        [Fact]
        public void Pump_ApplyingASnapshot_ClearsOrdersHardSetsAndVerifiesInOnePump()
        {
            var runtime = ClientRuntime(out _);
            bridge.Digest = "stale";
            bridge.DigestAfterApply = "abc";

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new SnapshotMessage(3, "abc", new[] { Snap("unit_a"), Snap("unit_b") }))));
            runtime.Pump(1);

            Assert.Equal(1, bridge.ClearLocalOrdersCalls);
            Assert.Equal(2, Assert.Single(bridge.AppliedSnapshots).Count);
            Assert.Contains(log.Lines, l => l.Contains("corrected") && l.Contains("OK"));
        }
        [Fact]
        public void Pump_WhenCorrectionDoesNotLand_ReportsStillDiverged()
        {
            // The honest failure: the two sides disagree about which units exist,
            // which no amount of position-setting can fix.
            var runtime = ClientRuntime(out _);
            bridge.Digest = "local";
            bridge.DigestAfterApply = "local";

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new SnapshotMessage(3, "host", new[] { Snap("unit_a") }))));
            runtime.Pump(1);

            Assert.Contains(log.Lines, l => l.Contains("STILL DIVERGED"));
        }


        // --- keyframe playback ---

        [Fact]
        public void Pump_ReceivingKeyframes_HandsThemToTheBridgeToPresent()
        {
            var runtime = ClientRuntime(out _);

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new KeyframesMessage(3, 15f, 20f, new[]
                {
                    new UnitTrack("unit_a", new[]
                    {
                        new TransformKey(15f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                        new TransformKey(20f, new Vec3(9f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    }),
                }))));
            runtime.Pump(1);

            var (turn, capture) = Assert.Single(bridge.Played);
            Assert.Equal(3, turn);
            Assert.Equal(20f, capture.WindowEnd);
            Assert.Equal("unit_a", Assert.Single(capture.Tracks).Name);
        }

        // The whole ordering claim in one pump: the correction lands and is
        // verified, and playback only then animates towards the same state. If
        // these were ever reordered the client would verify its digest against a
        // half-played animation.
        [Fact]
        public void Pump_SnapshotThenKeyframes_CorrectsBeforeItPresents()
        {
            var runtime = ClientRuntime(out _);
            bridge.Digest = "stale";
            bridge.DigestAfterApply = "abc";

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new SnapshotMessage(3, "abc", new[] { Snap("unit_a") }))));
            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new KeyframesMessage(3, 15f, 20f, new[] { new UnitTrack("unit_a", null) }))));
            runtime.Pump(1);

            Assert.Single(bridge.AppliedSnapshots);
            Assert.Single(bridge.Played);
            Assert.Contains(log.Lines, l => l.Contains("corrected") && l.Contains("OK"));
        }

        [Fact]
        public void Pump_ACombatLoadThatStarts_ReportsNothingYet()
        {
            // The answer comes later, through the glue's completion callback.
            var runtime = ClientRuntime(out _);
            bridge.CombatLoadRefusal = null;
            bridge.Scenario = new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2, 3, 4 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 5 }),
            });

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new CombatOfferMessage(LobbySaveNames.ScenarioSlot, bridge.Scenario.Digest, 4))));
            runtime.Pump(1);

            Assert.Single(bridge.CombatLoads);
            Assert.DoesNotContain(transport.MessagesTo(ClientSession.HostConnectionId),
                m => m is CombatEnteredMessage);
        }

        [Fact]
        public void Pump_ACombatLoadThatCannotStart_ReportsAtOnceRatherThanGoingQuiet()
        {
            // The host would otherwise wait out its whole timeout for an answer
            // this machine already has.
            var runtime = ClientRuntime(out _);
            bridge.CombatLoadRefusal = LoadOutcome.Unavailable;
            // Already holding these bytes, so the client loads rather than
            // fetching -- which is the path with a refusal to report.
            bridge.Scenario = new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2, 3, 4 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 5 }),
            });

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new CombatOfferMessage(LobbySaveNames.ScenarioSlot, bridge.Scenario.Digest, 4))));
            runtime.Pump(1);

            var report = transport.MessagesTo(ClientSession.HostConnectionId)
                .OfType<CombatEnteredMessage>().Single();
            Assert.Equal(LoadOutcome.Unavailable, report.Outcome);
        }

        [Fact]
        public void Pump_ReceivingABasePosition_HandsItToTheBridgeToPlace()
        {
            var runtime = ClientRuntime(out _);

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId,
                Frame(new BasePositionMessage(1024.5f, -37.25f))));
            runtime.Pump(1);

            var (x, z) = Assert.Single(bridge.Mirrored);
            Assert.Equal(1024.5f, x);
            Assert.Equal(-37.25f, z);
        }

        [Fact]
        public void Pump_CombatEnding_StopsPlaybackOnTheBridge()
        {
            var runtime = ClientRuntime(out _);

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, Frame(new CombatEndMessage())));
            runtime.Pump(1);

            Assert.Equal(1, bridge.StopKeyframesCalls);
        }

        [Fact]
        public void Snapshot_VerifiesRegardlessOfTheOrderUnitsAreApplied()
        {
            // StateDigest combines per-unit hashes by addition, so the client may
            // apply in any order and still match.
            var forwards = StateDigest.Compute(new[]
            {
                new UnitState("a", new Vec3(1f, 2f, 3f), 0.5f),
                new UnitState("b", new Vec3(4f, 5f, 6f), 0.25f),
            });
            var backwards = StateDigest.Compute(new[]
            {
                new UnitState("b", new Vec3(4f, 5f, 6f), 0.25f),
                new UnitState("a", new Vec3(1f, 2f, 3f), 0.5f),
            });

            Assert.Equal(forwards, backwards);
        }
    }
}
