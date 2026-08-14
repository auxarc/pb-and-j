using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Why a captured pose track cannot travel as it stands.
    /// </summary>
    /// <remarks>
    /// Split deliberately into two grades, because the two demand opposite
    /// responses and conflating them is what an adversarial review caught:
    /// <list type="bullet">
    /// <item><see cref="TooFewKeys"/> is <b>per track</b> and costs nothing. The
    /// game gates its whole pose block on more than two keys, so a track this
    /// short animates nothing on the <i>host</i> either — a client that skips it
    /// is showing what the host shows.</item>
    /// <item>Everything else is <b>per turn</b>. Dropping one unit's track for a
    /// fault of ours would put one sliding mech among walking ones, and a
    /// mixture is the one outcome this design forbids: the fallback has to be
    /// uniform or it reads as a bug rather than as lower fidelity.</item>
    /// </list>
    /// </remarks>
    public enum PoseTrackFault
    {
        /// <summary>Usable as it stands, or after repair.</summary>
        None = 0,

        /// <summary>
        /// Fewer than three keys survive. Per-track, and fidelity-neutral.
        /// </summary>
        TooFewKeys = 1,

        /// <summary>
        /// A key's joint count disagrees with the track's joint-name list.
        /// </summary>
        /// <remarks>
        /// Unrepairable: with the names and the values out of step there is no
        /// way to know which joint a value belongs to, and the game's playback
        /// loop indexes both densely.
        /// </remarks>
        Ragged = 2,

        /// <summary>
        /// A name is longer than <see cref="PbjMessageCodec.MaxPoseNameLength"/>.
        /// </summary>
        /// <remarks>
        /// Caught here rather than at encode on purpose. <c>PbjWriter</c> throws
        /// above its own string limit, and <c>PbjRuntime.SendTo</c> encodes
        /// <i>outside</i> its try block deliberately — so a throw there escapes
        /// the effect pump and takes every effect queued behind it, the
        /// oversize-blob failure M11e already paid for.
        /// </remarks>
        NameTooLong = 3,

        /// <summary>
        /// More joints than <see cref="PbjMessageCodec.MaxJointsPerPose"/>.
        /// </summary>
        /// <remarks>
        /// Unrepairable by thinning: keys can be dropped, a skeleton cannot.
        /// </remarks>
        TooManyJoints = 4,
    }

    /// <summary>
    /// Preparing captured pose tracks for the wire, and re-shaping a received
    /// one to the client's own skeleton.
    /// </summary>
    /// <remarks>
    /// Pure, and here rather than in the glue for the same reason
    /// <see cref="KeyframePlayback"/> is: every rule below is one no in-game
    /// eyeball would catch failing. A mis-ordered remap is a mech with its elbow
    /// on its knee, and an over-long track is not a visual defect at all — it is
    /// a frame the receiver rejects as malformed, which drops the peer.
    /// </remarks>
    public static class PoseTracks
    {
        /// <summary>Absent from the host's recording.</summary>
        public const int NoSource = -1;

        /// <summary>
        /// Where each of the client's own bones is to be found in the host's
        /// joint array, or <see cref="NoSource"/>.
        /// </summary>
        /// <remarks>
        /// Indexed by <b>client</b> bone, because that is the shape the game's
        /// playback loop demands: it reads the client's own
        /// <c>recordedBones.Count</c> and indexes the joint array to that bound
        /// with no guard at all.
        /// <para>
        /// Repeated names match <b>ordinally</b> — the client's nth
        /// <c>joint_pitch_mid</c> takes the host's nth. Names really do repeat:
        /// a leg group appends its joints per leg from cloned prefabs, so every
        /// multi-legged unit carries duplicates by design. Rejecting duplicates
        /// was the first design here, and it would have left exactly those units
        /// unposed on clients while the host animated them.
        /// </para>
        /// </remarks>
        public static int[] Remap(
            IReadOnlyList<string>? hostJoints, IReadOnlyList<string>? clientJoints)
        {
            var client = clientJoints ?? EmptyNames;
            var host = hostJoints ?? EmptyNames;

            var map = new int[client.Count];
            var occurrences = new Dictionary<string, List<int>>(host.Count);
            for (var i = 0; i < host.Count; i++)
            {
                var name = host[i];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (!occurrences.TryGetValue(name, out var at))
                {
                    at = new List<int>(1);
                    occurrences[name] = at;
                }
                at.Add(i);
            }

            var taken = new Dictionary<string, int>(client.Count);
            for (var i = 0; i < client.Count; i++)
            {
                map[i] = NoSource;
                var name = client[i];
                if (string.IsNullOrEmpty(name) || !occurrences.TryGetValue(name!, out var at))
                {
                    continue;
                }

                taken.TryGetValue(name!, out var used);
                if (used < at.Count)
                {
                    map[i] = at[used];
                }
                taken[name!] = used + 1;
            }

            return map;
        }

        /// <summary>
        /// Repairs one captured track, or says why it cannot be repaired.
        /// </summary>
        /// <remarks>
        /// Repair before rejection, throughout. The only key this drops is a
        /// leading duplicate timestamp, and that one is not cosmetic: the game's
        /// pose scan reaches key <c>k</c> only by having passed
        /// <c>keys[k-1].time &lt; requested</c>, so every interpolation span is
        /// strictly positive <i>except the first</i>, whose predecessor is never
        /// tested. A head pair sharing a timestamp therefore divides by zero at
        /// exactly the moment playback starts, and NaN bone transforms are how
        /// a unit disappears in Unity.
        /// <para>
        /// Interior and trailing duplicates are left alone. They are harmless by
        /// the same argument, and thinning them would be a change M6's own
        /// playback deliberately does not make.
        /// </para>
        /// </remarks>
        public static PoseTrackFault TryPrepare(UnitPoseTrack? track, out UnitPoseTrack? prepared)
        {
            prepared = null;
            if (track == null)
            {
                return PoseTrackFault.TooFewKeys;
            }

            if (Overlong(track.Name))
            {
                return PoseTrackFault.NameTooLong;
            }
            if (track.Joints.Count > PbjMessageCodec.MaxJointsPerPose)
            {
                return PoseTrackFault.TooManyJoints;
            }
            for (var i = 0; i < track.Joints.Count; i++)
            {
                if (Overlong(track.Joints[i]))
                {
                    return PoseTrackFault.NameTooLong;
                }
            }

            var keys = track.Keys;
            for (var i = 0; i < keys.Count; i++)
            {
                if (keys[i].Joints.Count != track.Joints.Count)
                {
                    return PoseTrackFault.Ragged;
                }
            }

            // The head-equal drop, and only it.
            var start = keys.Count > 1 && keys[0].Time >= keys[1].Time ? 1 : 0;
            var length = keys.Count - start;
            if (length <= 2)
            {
                return PoseTrackFault.TooFewKeys;
            }

            var kept = new PoseKey[length];
            for (var i = 0; i < length; i++)
            {
                kept[i] = keys[start + i];
            }

            prepared = new UnitPoseTrack(
                track.Name, track.Joints, Thin(kept, PbjMessageCodec.MaxPoseKeysPerTrack));
            return PoseTrackFault.None;
        }

        /// <summary>
        /// Drops interior keys until a track fits, keeping both endpoints.
        /// </summary>
        /// <remarks>
        /// Not a theoretical guard, which is why it repairs rather than
        /// rejecting. Replay sampling frequency is a <b>player-facing graphics
        /// setting</b> whose floor is 0.016 s, so a five-second turn can record
        /// well over three hundred poses — far past the cap — on a host that has
        /// done nothing but move a slider. Left un-thinned, the receiving peer
        /// rejects the frame as malformed and drops the host, every turn.
        /// <para>
        /// The tail is what must survive: the last key is where the snapshot has
        /// already corrected everyone to, and a track truncated at the end would
        /// finish somewhere the unit no longer is.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<PoseKey> Thin(IReadOnlyList<PoseKey> keys, int cap)
        {
            if (keys.Count <= cap)
            {
                return keys;
            }

            var kept = new PoseKey[cap];
            kept[0] = keys[0];
            var interior = cap - 2;
            var step = (keys.Count - 2) / (double)interior;
            for (var i = 0; i < interior; i++)
            {
                kept[1 + i] = keys[1 + (int)(i * step)];
            }
            kept[cap - 1] = keys[keys.Count - 1];
            return kept;
        }

        private static bool Overlong(string? name)
        {
            return name != null && name.Length > PbjMessageCodec.MaxPoseNameLength;
        }

        private static readonly string[] EmptyNames = new string[0];
    }
}
