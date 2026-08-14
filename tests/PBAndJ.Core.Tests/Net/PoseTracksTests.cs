using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PoseTracksTests
    {
        private static PoseKey Key(float time, int joints)
        {
            var values = new JointPose[joints];
            for (var j = 0; j < joints; j++)
            {
                values[j] = new JointPose(new Vec3(j, 0f, 0f), new Vec4(0f, 0f, 0f, 1f));
            }
            return new PoseKey(time, false, false, values);
        }

        private static UnitPoseTrack Track(int joints, params float[] times)
        {
            var names = new string[joints];
            for (var i = 0; i < joints; i++)
            {
                names[i] = "joint_" + i;
            }

            var keys = new PoseKey[times.Length];
            for (var k = 0; k < times.Length; k++)
            {
                keys[k] = Key(times[k], joints);
            }
            return new UnitPoseTrack("pb_mech_01", names, keys);
        }

        // --- Remap ---

        [Fact]
        public void Remap_MatchesByName_NotByPosition()
        {
            var map = PoseTracks.Remap(
                new[] { "hip", "knee", "foot" },
                new[] { "foot", "hip", "knee" });

            Assert.Equal(new[] { 2, 0, 1 }, map);
        }

        [Fact]
        public void Remap_ClientBoneTheHostNeverRecorded_HasNoSource()
        {
            var map = PoseTracks.Remap(new[] { "hip" }, new[] { "hip", "tail" });

            Assert.Equal(new[] { 0, PoseTracks.NoSource }, map);
        }

        // Names really do repeat: a leg group appends the same three joint names
        // once per leg from cloned prefabs. Rejecting duplicates was the first
        // design, and it would have left every multi-legged unit unposed on a
        // client while the host animated it perfectly.
        [Fact]
        public void Remap_RepeatedNames_PairUpInOrder()
        {
            var legs = new[] { "yaw", "pitch", "yaw", "pitch", "yaw", "pitch" };

            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, PoseTracks.Remap(legs, legs));
        }

        [Fact]
        public void Remap_MoreOfANameOnTheClientThanTheHostRecorded_LeavesTheExtrasUnsourced()
        {
            var map = PoseTracks.Remap(new[] { "yaw" }, new[] { "yaw", "yaw" });

            Assert.Equal(new[] { 0, PoseTracks.NoSource }, map);
        }

        [Fact]
        public void Remap_EmptyNamesOnEitherSide_AreNeverMatched()
        {
            var map = PoseTracks.Remap(new[] { "", "hip" }, new[] { "", "hip" });

            Assert.Equal(new[] { PoseTracks.NoSource, 1 }, map);
        }

        [Fact]
        public void Remap_NullLists_ProduceAnEmptyMap()
        {
            Assert.Empty(PoseTracks.Remap(null, null));
            Assert.Equal(new[] { PoseTracks.NoSource }, PoseTracks.Remap(null, new[] { "hip" }));
            Assert.Empty(PoseTracks.Remap(new[] { "hip" }, null));
        }

        // --- TryPrepare ---

        [Fact]
        public void TryPrepare_AWellFormedTrack_PassesThrough()
        {
            var fault = PoseTracks.TryPrepare(Track(2, 0f, 0.1f, 0.2f), out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Equal(3, prepared!.Keys.Count);
        }

        [Fact]
        public void TryPrepare_NullTrack_IsTooFewKeys()
        {
            Assert.Equal(PoseTrackFault.TooFewKeys, PoseTracks.TryPrepare(null, out var prepared));
            Assert.Null(prepared);
        }

        // The game gates its whole pose block on more than two keys, so a track
        // this short animates nothing on the host either. Skipping it shows the
        // client what the host sees, which is why this fault alone is allowed to
        // drop one unit rather than the turn.
        [Fact]
        public void TryPrepare_TooShortToAnimate_IsTooFewKeys()
        {
            Assert.Equal(
                PoseTrackFault.TooFewKeys, PoseTracks.TryPrepare(Track(2, 0f, 0.1f), out _));
        }

        // The one key this drops, and the only reason it drops it. The game's
        // pose scan reaches key k only by having passed keys[k-1].time < t, so
        // every span is strictly positive EXCEPT the first, whose predecessor is
        // never tested. A head pair sharing a timestamp divides by zero at
        // exactly the moment playback starts.
        [Fact]
        public void TryPrepare_HeadPairSharingATimestamp_DropsTheEarlierKey()
        {
            var fault = PoseTracks.TryPrepare(Track(2, 5f, 5f, 5.1f, 5.2f), out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Equal(3, prepared!.Keys.Count);
            Assert.Equal(5f, prepared.Keys[0].Time);
            Assert.Equal(5.1f, prepared.Keys[1].Time);
        }

        // Interior and trailing duplicates are left alone: the scan cannot reach
        // them with a zero span, and thinning them would diverge from what M6's
        // own playback deliberately keeps.
        [Fact]
        public void TryPrepare_InteriorDuplicateTimestamps_AreLeftAlone()
        {
            PoseTracks.TryPrepare(Track(2, 0f, 0.1f, 0.1f, 0.2f), out var prepared);

            Assert.Equal(4, prepared!.Keys.Count);
        }

        [Fact]
        public void TryPrepare_HeadDropLeavingTooFewKeys_IsTooFewKeys()
        {
            Assert.Equal(
                PoseTrackFault.TooFewKeys, PoseTracks.TryPrepare(Track(2, 1f, 1f, 2f), out _));
        }

        [Fact]
        public void TryPrepare_AKeyDisagreeingWithTheJointNames_IsRagged()
        {
            var track = new UnitPoseTrack(
                "u", new[] { "a", "b" }, new[] { Key(0f, 2), Key(1f, 1), Key(2f, 2) });

            Assert.Equal(PoseTrackFault.Ragged, PoseTracks.TryPrepare(track, out _));
        }

        [Fact]
        public void TryPrepare_MoreJointsThanTheCap_IsTooManyJoints()
        {
            var names = new string[PbjMessageCodec.MaxJointsPerPose + 1];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = "j" + i;
            }

            Assert.Equal(
                PoseTrackFault.TooManyJoints,
                PoseTracks.TryPrepare(new UnitPoseTrack("u", names, null), out _));
        }

        // Caught here rather than at encode because PbjWriter throws above its
        // string limit and PbjRuntime.SendTo encodes outside its try block — so
        // a name that overstepped would not fail this message, it would empty
        // the effect pump queued behind it.
        [Fact]
        public void TryPrepare_AnOverlongUnitName_IsNameTooLong()
        {
            var name = new string('u', PbjMessageCodec.MaxPoseNameLength + 1);
            var track = new UnitPoseTrack(name, new[] { "a" }, null);

            Assert.Equal(PoseTrackFault.NameTooLong, PoseTracks.TryPrepare(track, out _));
        }

        [Fact]
        public void TryPrepare_AnOverlongJointName_IsNameTooLong()
        {
            var joint = new string('j', PbjMessageCodec.MaxPoseNameLength + 1);
            var track = new UnitPoseTrack("u", new[] { "a", joint }, null);

            Assert.Equal(PoseTrackFault.NameTooLong, PoseTracks.TryPrepare(track, out _));
        }

        [Fact]
        public void TryPrepare_ANullUnitName_IsAcceptedLikeM6sTracks()
        {
            var track = new UnitPoseTrack(null, new[] { "a" }, new[]
            {
                Key(0f, 1), Key(1f, 1), Key(2f, 1),
            });

            Assert.Equal(PoseTrackFault.None, PoseTracks.TryPrepare(track, out _));
        }

        [Fact]
        public void TryPrepare_MoreKeysThanTheCap_IsThinnedRatherThanRejected()
        {
            var times = new float[PbjMessageCodec.MaxPoseKeysPerTrack + 40];
            for (var i = 0; i < times.Length; i++)
            {
                times[i] = i * 0.016f;
            }

            var fault = PoseTracks.TryPrepare(Track(2, times), out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Equal(PbjMessageCodec.MaxPoseKeysPerTrack, prepared!.Keys.Count);
        }

        // --- Thin ---

        [Fact]
        public void Thin_ATrackThatAlreadyFits_IsReturnedUntouched()
        {
            var keys = new List<PoseKey> { Key(0f, 1), Key(1f, 1) };

            Assert.Same(keys, PoseTracks.Thin(keys, 8));
        }

        // The tail is the key that must survive: it is where the snapshot has
        // already corrected everyone to, so a track truncated at the end would
        // finish somewhere the unit no longer is.
        [Fact]
        public void Thin_KeepsBothEndpoints()
        {
            var keys = new PoseKey[50];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = Key(i, 1);
            }

            var thinned = PoseTracks.Thin(keys, 10);

            Assert.Equal(10, thinned.Count);
            Assert.Equal(0f, thinned[0].Time);
            Assert.Equal(49f, thinned[9].Time);
        }

        [Fact]
        public void Thin_KeepsTheInteriorAscending()
        {
            var keys = new PoseKey[313];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = Key(i * 0.016f, 1);
            }

            var thinned = PoseTracks.Thin(keys, PbjMessageCodec.MaxPoseKeysPerTrack);

            for (var i = 1; i < thinned.Count; i++)
            {
                Assert.True(thinned[i].Time >= thinned[i - 1].Time,
                    $"key {i} went backwards in time");
            }
        }
    }
}
