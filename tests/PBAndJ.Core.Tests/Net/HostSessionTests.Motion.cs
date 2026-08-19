using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Two sections of the original: keyframes (M6) and poses (M8). They are together
    // because a pose track and a transform track are broadcast in one burst, and
    // several tests here turn on their ordering relative to each other.
    //
    // Posed, MotionWith and PosesOf are called only from here. The helper named
    // Motion() is NOT in this file despite the filename: 4 of its 6 call sites are
    // here and 2 are in .Effects.cs, so it is shared fixture in the primary.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- keyframes (M6) ---

        // Keyframes are presentation; the snapshot is the correction the digest
        // is checked against. The correction must never queue behind them.
        [Fact]
        public void TurnComplete_BroadcastsKeyframesAfterTheSnapshot()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()))
                .ToList();

            var snapshotAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is SnapshotMessage);
            var keyframesAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is KeyframesMessage);
            Assert.True(snapshotAt >= 0 && keyframesAt > snapshotAt);
        }

        [Fact]
        public void TurnComplete_KeyframesCarryTheExecutedTurnAndTheWindow()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()));
            var keyframes = (KeyframesMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is KeyframesMessage).Message;

            Assert.Equal(3, keyframes.Turn);
            Assert.Equal(15f, keyframes.WindowStart);
            Assert.Equal(20f, keyframes.WindowEnd);
            Assert.Equal("unit_a", Assert.Single(keyframes.Tracks).Name);
        }

        // A scenario with prediction disabled records nothing. That must cost an
        // empty broadcast, not an empty message.
        [Fact]
        public void TurnComplete_WithNothingRecorded_BroadcastsNoKeyframes()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", null, null));

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is SnapshotMessage);
        }

        // --- poses (M8) ---

        private static UnitPoseTrack Posed(string unit, int keys = 3, int joints = 1)
        {
            var names = new string[joints];
            for (var j = 0; j < joints; j++)
            {
                names[j] = "joint_" + j;
            }

            var poses = new PoseKey[keys];
            for (var k = 0; k < keys; k++)
            {
                var values = new JointPose[joints];
                for (var j = 0; j < joints; j++)
                {
                    values[j] = new JointPose(default, default);
                }
                poses[k] = new PoseKey(15f + k, false, false, values);
            }

            return new UnitPoseTrack(unit, names, poses);
        }

        private static KeyframeCapture MotionWith(params UnitPoseTrack[] poses) =>
            new KeyframeCapture(15f, 20f, Motion().Tracks, poses);

        private static IReadOnlyList<PosesMessage> PosesOf(IEnumerable<PbjEffect> effects) =>
            All<BroadcastEffect>(effects).Select(b => b.Message).OfType<PosesMessage>().ToList();

        // The ordering the whole arrival model rests on. Poses first, keyframes
        // last: the client cannot decide whether it has a complete set until
        // something tells it the burst is over, and the transform message is
        // that terminator. Reverse these and the client needs a deadline.
        [Fact]
        public void TurnComplete_BroadcastsPosesBeforeTheKeyframesThatTerminateThem()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent(
                    "abc", new[] { Snap("unit_a") }, MotionWith(Posed("unit_a"))))
                .ToList();

            var posesAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is PosesMessage);
            var keyframesAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is KeyframesMessage);
            Assert.True(posesAt >= 0 && keyframesAt > posesAt);
        }

        [Fact]
        public void TurnComplete_SendsOnePartPerUnitNumberedAgainstTheWhole()
        {
            var parts = PosesOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") }, MotionWith(Posed("unit_a"), Posed("unit_b")))));

            Assert.Equal(2, parts.Count);
            Assert.Equal(new[] { 0, 1 }, parts.Select(p => p.PartIndex));
            Assert.All(parts, p => Assert.Equal(2, p.PartCount));
            Assert.All(parts, p => Assert.Equal(3, p.Turn));
        }

        [Fact]
        public void TurnComplete_WithNoPosesRecorded_BroadcastsNoParts()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()));

            Assert.Empty(PosesOf(effects));
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
        }

        // Poses live inside the same guard as the terminator. A turn with no
        // transform tracks sends neither, because parts that nothing ever
        // terminates are the one shape the client cannot resolve — it would hold
        // them until some later turn's terminator threw them away.
        [Fact]
        public void TurnComplete_WithPosesButNoTransformTracks_SendsNothingAtAll()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                new KeyframeCapture(15f, 20f, null, new[] { Posed("unit_a") })));

            Assert.Empty(PosesOf(effects));
            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
        }

        [Fact]
        public void TurnComplete_ThinsAnOversampledTrackRatherThanRefusingIt()
        {
            var parts = PosesOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWith(Posed("unit_a", PbjMessageCodec.MaxPoseKeysPerTrack + 60)))));

            Assert.Equal(
                PbjMessageCodec.MaxPoseKeysPerTrack, Assert.Single(parts).Track!.Keys.Count);
        }

        // A track too short to animate is dropped on its own, and that is the
        // only per-track drop allowed: the game gates its pose block on more
        // than two keys, so the host does not animate this unit either.
        [Fact]
        public void TurnComplete_ATrackTooShortToAnimate_DropsThatUnitAlone()
        {
            var parts = PosesOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWith(Posed("unit_a"), Posed("unit_b", keys: 2)))));

            Assert.Equal("unit_a", Assert.Single(parts).Track!.Name);
            Assert.Equal(1, parts[0].PartCount);
        }

        // Everything else demotes the WHOLE turn. One unit sliding among walking
        // ones reads as a broken game; every unit sliding reads as the lower
        // fidelity mode it is. Uniformity is the point, not tidiness.
        [Fact]
        public void TurnComplete_AnUnrepairableTrack_DemotesTheEntireTurnToTransformOnly()
        {
            var joints = new string[PbjMessageCodec.MaxJointsPerPose + 1];
            for (var i = 0; i < joints.Length; i++)
            {
                joints[i] = "j" + i;
            }

            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWith(Posed("unit_a"), new UnitPoseTrack("unit_b", joints, null)))).ToList();

            Assert.Empty(PosesOf(effects));
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("poses dropped"));
        }

        [Fact]
        public void TurnComplete_PosesAreReportedWithTheirCount()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") }, MotionWith(Posed("unit_a"))));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("1 unit track"));
        }
    }
}
