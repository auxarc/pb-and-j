using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Threading;
using PBAndJ.Core.Net;
using PBAndJ.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// Synthetic-data builders and the rotation table they share.
    /// </summary>
    /// <remarks>
    /// One part of <c>SelfTest</c>, which is a single class split across
    /// files. The scenario table in SelfTest.cs is checked against the
    /// methods declared here at run time, so a part whose registration is
    /// lost fails loudly rather than silently running fewer scenarios.
    /// </remarks>
    internal static partial class SelfTest
    {
        /// <summary>
        /// A synthetic turn of replayed effects, every value distinct. M14.
        /// </summary>
        /// <remarks>
        /// Distinctness for the reason <see cref="BuildPoseTrack"/> needs it, and
        /// more sharply: a projectile whose position and rotation were
        /// transposed flies sideways, and a standalone effect whose scale was
        /// lost renders at zero size — invisible, and indistinguishable from the
        /// feature not working. Neither shows in a count or a log line, so the
        /// comparison has to be field for field on values that cannot coincide.
        /// <para>
        /// The hue and colour blocks alternate present and absent across the
        /// standalone tracks on purpose. Absence is a real instruction — leave
        /// the prefab's own hue alone — and it is carried by a flag rather than
        /// a sentinel, so a codec that wrote the flag and forgot the payload
        /// would round-trip every present block and silently flatten the absent
        /// ones into zeroes.
        /// </para>
        /// </remarks>
        private static AssetCapture BuildAssetCapture(
            int seed,
            float windowStart,
            float windowEnd,
            int standaloneCount,
            int projectileCount,
            int beamCount,
            int keyCount = 3)
        {
            float At(int k) => keyCount > 1
                ? windowStart + ((windowEnd - windowStart) * k / (keyCount - 1f))
                : windowStart;

            var standalone = new StandaloneAssetTrack[standaloneCount];
            for (var i = 0; i < standaloneCount; i++)
            {
                var v = (seed * 1000f) + (i * 7f);
                standalone[i] = new StandaloneAssetTrack(
                    i,
                    new AssetTrackHead(
                        $"fx_impact_{seed}_{i}", windowStart + i, windowEnd + i,
                        i % 2 == 0 ? (float?)(i * 0.125f) : null,
                        i % 3 == 0
                            ? new AssetColour(
                                new Vec4(v, v + 1f, v + 2f, v + 3f),
                                new Vec4(v + 4f, v + 5f, v + 6f, v + 7f))
                            : (AssetColour?)null),
                    new Vec3(v, v + 0.25f, v + 0.5f),
                    UnitRotations[i % UnitRotations.Length],
                    new Vec3(v + 1f, v + 1.25f, v + 1.5f),
                    new Vec4(v + 2f, v + 2.25f, v + 2.5f, v + 2.75f),
                    new Vec3(v + 3f, v + 3.25f, v + 3.5f));
            }

            var projectiles = new ProjectileAssetTrack[projectileCount];
            for (var i = 0; i < projectileCount; i++)
            {
                var keys = new TransformKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    var v = (seed * 1000f) + (i * 7f) + (k * 0.75f);
                    keys[k] = new TransformKey(
                        At(k), new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(i + k) % UnitRotations.Length]);
                }
                // Stage B: only some projectiles carry a trail, matching the
                // measured 3-in-109 rather than giving every projectile one. A
                // codec that mixed the trail list up with the transform list
                // would pass a suite where every track looked alike.
                TrailKey[]? trail = null;
                if (i % 2 == 0)
                {
                    trail = new TrailKey[keyCount + 1];
                    for (var t = 0; t < trail.Length; t++)
                    {
                        var w = (seed * 100f) + (i * 13f) + (t * 1.5f);
                        trail[t] = new TrailKey(
                            windowStart + (t * 0.05f),
                            windowStart + (t * 0.05f) + 0.4f,
                            new Vec3(w, w + 1f, w + 2f),
                            new Vec3(w + 3f, w + 4f, w + 5f),
                            new Vec3(w + 6f, w + 7f, w + 8f),
                            new Vec3(w + 9f, w + 10f, w + 11f),
                            new Vec3(w + 12f, w + 13f, w + 14f),
                            new Vec4(w + 15f, w + 16f, w + 17f, w + 18f),
                            w + 19f,
                            w + 20f);
                    }
                }

                projectiles[i] = new ProjectileAssetTrack(
                    i,
                    new AssetTrackHead($"fx_bullet_{seed}_{i}", windowStart, windowEnd + 1f),
                    new Vec3(1f + i, 2f + i, 3f + i),
                    keys,
                    trail);
            }

            var beams = new BeamAssetTrack[beamCount];
            for (var i = 0; i < beamCount; i++)
            {
                var keys = new BeamKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    var v = (seed * 1000f) + (i * 7f) + (k * 0.75f);
                    keys[k] = new BeamKey(
                        At(k), new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(i + k) % UnitRotations.Length],
                        new Vec3(v + 5f, v + 5.25f, v + 5.5f));
                }
                beams[i] = new BeamAssetTrack(
                    i, new AssetTrackHead($"fx_beam_{seed}_{i}", windowStart, windowEnd), keys);
            }

            return new AssetCapture(standalone, projectiles, beams);
        }

        /// <summary>
        /// A synthetic pose track whose every value is distinct.
        /// </summary>
        /// <remarks>
        /// Distinctness is the whole design of it. A codec that transposed two
        /// joints, two keys or two tracks would round-trip a track built from
        /// repeated values perfectly, and the wire assertions would pass while
        /// the client put a mech's elbow on its knee.
        /// <para>
        /// The last joint name deliberately repeats the one before it. Duplicate
        /// joint names are not a malformed input — a leg group appends its joints
        /// per leg from cloned prefabs, so every multi-legged unit carries them —
        /// and <see cref="PoseTracks.Remap"/> matches them ordinally. A harness
        /// that only ever sent unique names would leave that untested.
        /// </para>
        /// <para>
        /// Rotations come from the four axis-aligned unit quaternions rather than
        /// from anything computed, because the sampler normalises: a rotation
        /// that is only nearly unit-length would come back nearly equal, and this
        /// scenario compares exactly.
        /// </para>
        /// </remarks>
        private static UnitPoseTrack BuildPoseTrack(
            string? name, int seed, float windowStart, float windowEnd, int keyCount, int jointCount)
        {
            var joints = new string[jointCount];
            for (var j = 0; j < jointCount; j++)
            {
                joints[j] = j == jointCount - 1 && jointCount > 1
                    ? joints[j - 1]
                    : $"joint_{j}";
            }

            var keys = new PoseKey[keyCount];
            for (var k = 0; k < keyCount; k++)
            {
                var time = keyCount > 1
                    ? windowStart + ((windowEnd - windowStart) * k / (keyCount - 1f))
                    : windowStart;

                var poses = new JointPose[jointCount];
                for (var j = 0; j < jointCount; j++)
                {
                    var v = (seed * 100f) + (k * 10f) + (j * 0.5f);
                    poses[j] = new JointPose(
                        new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(k + j) % UnitRotations.Length]);
                }

                // Both flags vary, and independently: they pin the weapons to the
                // palms, so a codec that dropped or conflated one bit would leave
                // a rifle hanging in mid-air through the firing animation.
                keys[k] = new PoseKey(time, k % 2 == 0, k >= keyCount / 2, poses);
            }

            // M14 stage C rides the same track, so it is exercised by the same
            // leg rather than by a scenario of its own. Two pings and one swing:
            // enough that a codec reading either count in the wrong place walks
            // off into the next field, which is the failure this catches.
            var reactions = new[]
            {
                windowStart + ((windowEnd - windowStart) * 0.25f),
                windowStart + ((windowEnd - windowStart) * 0.75f),
            };

            var melees = new[]
            {
                new MeleeTrajectory(
                    windowStart + 0.5f,
                    windowEnd - 0.5f,
                    seed % 2 == 0,
                    $"shockwave_{seed}",
                    new Vec3(seed, seed + 1f, seed + 2f),
                    new Vec3(seed + 3f, seed + 4f, seed + 5f)),
            };

            return new UnitPoseTrack(name, joints, keys, null, reactions, melees);
        }


        private static readonly Vec4[] UnitRotations =
        {
            new Vec4(0f, 0f, 0f, 1f),
            new Vec4(0f, 1f, 0f, 0f),
            new Vec4(1f, 0f, 0f, 0f),
            new Vec4(0f, 0f, 1f, 0f),
        };
    }
}
