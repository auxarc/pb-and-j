using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Everything the client plays back from the host: keyframes (M6), poses (M8)
    // and replayed effects (M14).
    // The three share a part because they share a helper -- Motion builds the
    // keyframe message that the effects tests also drive playback with, and its
    // seventeen call sites span all three sections. Splitting them would have
    // pushed Motion into the shared fixture to no one's benefit.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- keyframes (M6) ---

        private static KeyframesMessage Motion(int turn = 3) =>
            new KeyframesMessage(turn, 15f, 20f, new[]
            {
                new UnitTrack("unit_b", new[]
                {
                    new TransformKey(15f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(20f, new Vec3(9f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            });

        [Fact]
        public void Keyframes_StartPlaybackCarryingTheTurnAndTheWindow()
        {
            var play = Single<PlayKeyframesEffect>(
                Welcomed().HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.Equal(3, play.Turn);
            Assert.Equal(15f, play.Capture.WindowStart);
            Assert.Equal(20f, play.Capture.WindowEnd);
            Assert.Equal("unit_b", Assert.Single(play.Capture.Tracks).Name);
        }

        [Fact]
        public void Keyframes_AreReported()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, Motion());
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("keyframes received"));
        }

        // Playback is presentation only, so receiving it must not move the
        // client's own idea of the turn or unlock anything.
        [Fact]
        public void Keyframes_ChangeNoSessionState()
        {
            var client = Welcomed();
            var before = client.State;
            var turn = client.Turn;

            client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Equal(before, client.State);
            Assert.Equal(turn, client.Turn);
        }

        // --- poses (M8) ---

        private static PosesMessage Pose(int turn, int index, int count, string unit) =>
            new PosesMessage(turn, index, count, new UnitPoseTrack(unit, new[] { "j" }, new[]
            {
                new PoseKey(15f, false, false, new[] { new JointPose(default, default) }),
                new PoseKey(17f, true, false, new[] { new JointPose(default, default) }),
                new PoseKey(20f, false, true, new[] { new JointPose(default, default) }),
            }));

        [Fact]
        public void Poses_ArrivingBeforeTheKeyframes_ReachPlaybackWithThem()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));

            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.Equal("unit_b", Assert.Single(play.Capture.Poses).Name);
        }

        // Poses accumulate and decide nothing. Playback begins on the
        // terminator, because a unit slept for replay while its pose has not
        // landed is a rigid statue, which is worse than M6's slide rather than
        // equal to it.
        [Fact]
        public void Poses_AloneStartNoPlayback()
        {
            var effects = Welcomed().HandleMessage(
                ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));

            Assert.Empty(All<PlayKeyframesEffect>(effects));
        }

        [Fact]
        public void Poses_ThatNeverCompleted_FallBackToTransformOnly()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 3, "unit_b"));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Empty(Single<PlayKeyframesEffect>(effects).Capture.Poses);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("poses incomplete"));
        }

        [Fact]
        public void Poses_ThatCompleted_AreReported()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("poses complete"));
        }

        [Fact]
        public void Poses_ForADifferentTurnThanTheKeyframes_AreNotPlayed()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(2, 0, 1, "unit_b"));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion(3));

            Assert.Empty(Single<PlayKeyframesEffect>(effects).Capture.Poses);
        }

        // The trap this design nearly walked into. TurnComplete travels ahead of
        // the poses and advances the session's own turn, so parts labelled T
        // always arrive while the session already reads T+1. Anything comparing
        // against session state would discard every part of every turn, and the
        // only symptom would be that poses silently never appear.
        [Fact]
        public void Poses_ArrivingAfterTurnCompleteHasAdvancedTheTurn_StillPlay()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCompleteMessage(3, "d"));
            Assert.Equal(4, client.Turn);

            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));
            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion(3)));

            Assert.Single(play.Capture.Poses);
        }

        [Fact]
        public void Poses_HeldWhenCombatEnds_AreForgotten()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));
            client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());

            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.Empty(play.Capture.Poses);
        }

        // Bye and a fault both drop the held poses too, but neither can be
        // asserted the way the CombatEnd case above is: those two end the
        // session, so nothing afterwards is handled at all and no later playback
        // exists to inspect. The clearing is memory hygiene there rather than
        // correctness — a quarter of a megabyte per turn, held by a session that
        // will never be spoken to again. What is worth pinning is that the
        // session really has gone inert, since that is the premise.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Poses_AfterTheSessionEnds_CanReachNoPlaybackAtAll(bool byGoodbye)
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Pose(3, 0, 1, "unit_b"));

            if (byGoodbye)
            {
                client.HandleMessage(ClientSession.HostConnectionId, new ByeMessage("done"));
            }
            else
            {
                client.Handle(new TransportFailedEvent("socket died"));
            }

            Assert.Empty(All<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion())));
        }

        // --- replayed effects (M14) ---

        private static ReplayAssetsMessage Effects(int turn, int index, int count, int id) =>
            new ReplayAssetsMessage(turn, index, count, new AssetCapture(
                new[]
                {
                    new StandaloneAssetTrack(
                        id, new AssetTrackHead("fx_impact", 15f, 16f), default, default,
                        new Vec3(1f, 1f, 1f), default, default),
                },
                null,
                null));

        [Fact]
        public void Effects_ArrivingBeforeTheKeyframes_ReachPlaybackWithThem()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Effects(3, 0, 1, 7));

            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.Equal(7, Assert.Single(play.Capture.Assets.Standalone).Id);
        }

        [Fact]
        public void Effects_AloneStartNoPlayback()
        {
            var effects = Welcomed().HandleMessage(
                ClientSession.HostConnectionId, Effects(3, 0, 1, 7));

            Assert.Empty(All<PlayKeyframesEffect>(effects));
        }

        [Fact]
        public void Effects_ThatCompleted_AreReported()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Effects(3, 0, 1, 7));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("effects complete"));
        }

        // An arbitrary slice of a turn's effects is worse than none: shots that
        // stop in mid air, with nothing in the log to say why. The line is
        // logged on both arms so "nothing shoots" always has an explanation.
        [Fact]
        public void Effects_ThatNeverCompleted_PlayNoneOfThemAndSayWhy()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Effects(3, 0, 3, 7));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.True(Single<PlayKeyframesEffect>(effects).Capture.Assets.IsEmpty);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("effects incomplete"));
        }

        // A turn where the host simply had nothing to send is the common case —
        // the measured fight's first turn had no contact at all — so it must
        // not be reported as a loss, or the line that reports a real one stops
        // being read.
        [Fact]
        public void Effects_ThatWereNeverSent_AreReportedAsAQuietTurnNotALoss()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("effects: none sent"));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("effects incomplete"));
        }

        [Fact]
        public void Effects_ForADifferentTurnThanTheKeyframes_AreNotPlayed()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Effects(2, 0, 1, 7));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Motion(3));

            Assert.True(Single<PlayKeyframesEffect>(effects).Capture.Assets.IsEmpty);
        }

        // The same trap the poses documented, and the reason both buffers
        // compare message labels against message labels: TurnComplete travels
        // ahead of these parts and advances the session's own turn.
        [Fact]
        public void Effects_ArrivingAfterTurnCompleteHasAdvancedTheTurn_StillPlay()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCompleteMessage(3, "d"));

            client.HandleMessage(ClientSession.HostConnectionId, Effects(3, 0, 1, 7));
            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion(3)));

            Assert.Single(play.Capture.Assets.Standalone);
        }

        // Both buffers are terminated by the same message, so they must be
        // abandoned by the same events. Forgetting one and not the other would
        // let a rejoin's first terminator consume the previous session's
        // explosions over the new one's opening move.
        [Fact]
        public void Effects_HeldWhenCombatEnds_AreForgotten()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Effects(3, 0, 1, 7));
            client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());

            var play = Single<PlayKeyframesEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.True(play.Capture.Assets.IsEmpty);
        }

        // A turn ending, a host vanishing or a session closing all leave a
        // playback mid-flight. Each one has to stop it, or units keep sliding
        // through whatever comes next.
        [Fact]
        public void CombatEnd_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void Bye_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, new ByeMessage("done"));
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void AFaultingHost_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().Handle(new TransportFailedEvent("socket died"));
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void SnapshotApplied_WithAMatchingDigest_ReportsTheCorrectionVerified()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("corrected") && l.Line.Contains("OK"));
        }

        [Fact]
        public void SnapshotApplied_WithAMismatchedDigest_ReportsItLoudly()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "def"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("STILL DIVERGED"));
        }

        [Fact]
        public void SnapshotApplied_ChangesNoState()
        {
            var client = Welcomed();
            client.Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }
    }
}
