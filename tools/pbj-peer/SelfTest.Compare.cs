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
    // Field-by-field equality helpers.
    //
    // One part of SelfTest, a single class split across files. Class-level
    // XML doc lives ONLY in SelfTest.cs: /// on a partial part is concatenated
    // by the compiler into one type entry, so eleven parts would produce
    // eleven summaries glued together. Caught by diffing the emitted XML.
    internal static partial class SelfTest
    {
        /// <summary>
        /// Whether a turn's effects came back exactly as they went out.
        /// </summary>
        /// <remarks>
        /// Matched by id rather than by position, because the parts they
        /// travelled in are reassembled by concatenation and an ordering bug is
        /// one of the things this is here to catch — comparing positionally
        /// would make the assertion agree with the bug.
        /// </remarks>
        private static bool SameAssets(AssetCapture sent, AssetCapture got, out string why)
        {
            why = string.Empty;

            if (got.Standalone.Count != sent.Standalone.Count
                || got.Projectiles.Count != sent.Projectiles.Count
                || got.Beams.Count != sent.Beams.Count)
            {
                why = $"arrived as {got.Standalone.Count}/{got.Projectiles.Count}/{got.Beams.Count} "
                    + $"tracks, not {sent.Standalone.Count}/{sent.Projectiles.Count}/{sent.Beams.Count}";
                return false;
            }

            foreach (var a in sent.Standalone)
            {
                StandaloneAssetTrack? b = null;
                foreach (var candidate in got.Standalone)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"standalone {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"standalone {a.Id}", ref why)
                    || !SameVec3(a.Position, b.Position, $"standalone {a.Id} position", ref why)
                    || !SameVec4(a.Rotation, b.Rotation, $"standalone {a.Id} rotation", ref why)
                    || !SameVec3(a.Scale, b.Scale, $"standalone {a.Id} scale", ref why)
                    || !SameVec4(
                        a.VelocityAndDecay, b.VelocityAndDecay, $"standalone {a.Id} velocity", ref why)
                    || !SameVec3(
                        a.PositionLocal, b.PositionLocal, $"standalone {a.Id} local position", ref why))
                {
                    return false;
                }
            }

            foreach (var a in sent.Projectiles)
            {
                ProjectileAssetTrack? b = null;
                foreach (var candidate in got.Projectiles)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"projectile {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"projectile {a.Id}", ref why)
                    || !SameVec3(a.Scale, b.Scale, $"projectile {a.Id} scale", ref why))
                {
                    return false;
                }
                if (b.Keys.Count != a.Keys.Count)
                {
                    why = $"projectile {a.Id} arrived with {b.Keys.Count} keys, not {a.Keys.Count}";
                    return false;
                }
                for (var k = 0; k < a.Keys.Count; k++)
                {
                    if (a.Keys[k].Time != b.Keys[k].Time
                        || !SameVec3(
                            a.Keys[k].Position, b.Keys[k].Position,
                            $"projectile {a.Id} key {k} position", ref why)
                        || !SameVec4(
                            a.Keys[k].Rotation, b.Keys[k].Rotation,
                            $"projectile {a.Id} key {k} rotation", ref why))
                    {
                        if (why.Length == 0)
                        {
                            why = $"projectile {a.Id} key {k} is stamped {b.Keys[k].Time}";
                        }
                        return false;
                    }
                }

                // Stage B. Compared point by point and in order, because order
                // IS the ribbon's geometry here: SetPoints treats the last point
                // as the head and snaps it to the instance, so a trail that
                // arrived reversed would match on every count and still render
                // inside out.
                if (b.Trail.Count != a.Trail.Count)
                {
                    why = $"projectile {a.Id} arrived with {b.Trail.Count} trail points, "
                        + $"not {a.Trail.Count}";
                    return false;
                }
                for (var t = 0; t < a.Trail.Count; t++)
                {
                    if (a.Trail[t].Time != b.Trail[t].Time
                        || a.Trail[t].TimeEnd != b.Trail[t].TimeEnd
                        || a.Trail[t].Thickness != b.Trail[t].Thickness
                        || a.Trail[t].Texcoord != b.Trail[t].Texcoord
                        || !SameVec3(
                            a.Trail[t].Position, b.Trail[t].Position,
                            $"projectile {a.Id} trail {t} position", ref why)
                        || !SameVec3(
                            a.Trail[t].Velocity, b.Trail[t].Velocity,
                            $"projectile {a.Id} trail {t} velocity", ref why)
                        || !SameVec3(
                            a.Trail[t].PerlinDirection, b.Trail[t].PerlinDirection,
                            $"projectile {a.Id} trail {t} perlin direction", ref why)
                        || !SameVec3(
                            a.Trail[t].Tangent, b.Trail[t].Tangent,
                            $"projectile {a.Id} trail {t} tangent", ref why)
                        || !SameVec3(
                            a.Trail[t].Normal, b.Trail[t].Normal,
                            $"projectile {a.Id} trail {t} normal", ref why)
                        || !SameVec4(
                            a.Trail[t].Colour, b.Trail[t].Colour,
                            $"projectile {a.Id} trail {t} colour", ref why))
                    {
                        if (why.Length == 0)
                        {
                            why = $"projectile {a.Id} trail {t} is stamped {b.Trail[t].Time}";
                        }
                        return false;
                    }
                }
            }

            foreach (var a in sent.Beams)
            {
                BeamAssetTrack? b = null;
                foreach (var candidate in got.Beams)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"beam {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"beam {a.Id}", ref why))
                {
                    return false;
                }
                if (b.Keys.Count != a.Keys.Count)
                {
                    why = $"beam {a.Id} arrived with {b.Keys.Count} keys, not {a.Keys.Count}";
                    return false;
                }
                for (var k = 0; k < a.Keys.Count; k++)
                {
                    if (a.Keys[k].Time != b.Keys[k].Time
                        || !SameVec3(
                            a.Keys[k].Position, b.Keys[k].Position,
                            $"beam {a.Id} key {k} position", ref why)
                        || !SameVec4(
                            a.Keys[k].Rotation, b.Keys[k].Rotation,
                            $"beam {a.Id} key {k} rotation", ref why)
                        || !SameVec3(
                            a.Keys[k].Parameters, b.Keys[k].Parameters,
                            $"beam {a.Id} key {k} parameters", ref why))
                    {
                        if (why.Length == 0)
                        {
                            why = $"beam {a.Id} key {k} is stamped {b.Keys[k].Time}";
                        }
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SameHead(AssetTrackHead a, AssetTrackHead b, string what, ref string why)
        {
            if (a.AssetKey != b.AssetKey)
            {
                why = $"{what} arrived keyed '{b.AssetKey}', not '{a.AssetKey}'";
                return false;
            }
            if (a.TimeStart != b.TimeStart || a.TimeEnd != b.TimeEnd)
            {
                why = $"{what} arrived spanning {b.TimeStart}..{b.TimeEnd}, not {a.TimeStart}..{a.TimeEnd}";
                return false;
            }

            // Absence and zero are different instructions, so HasValue is
            // compared before the value ever is.
            if (a.Hue.HasValue != b.Hue.HasValue
                || (a.Hue.HasValue && a.Hue!.Value != b.Hue!.Value))
            {
                why = $"{what} lost or invented a hue offset";
                return false;
            }
            if (a.Colour.HasValue != b.Colour.HasValue)
            {
                why = $"{what} lost or invented a colour";
                return false;
            }
            if (a.Colour.HasValue
                && (!SameVec4(a.Colour!.Value.From, b.Colour!.Value.From, $"{what} colour from", ref why)
                    || !SameVec4(a.Colour.Value.To, b.Colour.Value.To, $"{what} colour to", ref why)))
            {
                return false;
            }
            return true;
        }

        private static bool SameVec3(Vec3 a, Vec3 b, string what, ref string why)
        {
            if (a.X == b.X && a.Y == b.Y && a.Z == b.Z)
            {
                return true;
            }
            why = $"{what} became {b.X},{b.Y},{b.Z}, not {a.X},{a.Y},{a.Z}";
            return false;
        }

        private static bool SameVec4(Vec4 a, Vec4 b, string what, ref string why)
        {
            if (a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W)
            {
                return true;
            }
            why = $"{what} became {b.X},{b.Y},{b.Z},{b.W}, not {a.X},{a.Y},{a.Z},{a.W}";
            return false;
        }

        private static UnitPoseTrack? FindPose(IReadOnlyList<UnitPoseTrack> tracks, string? name)
        {
            foreach (var track in tracks)
            {
                if (track.Name == name)
                {
                    return track;
                }
            }
            return null;
        }

        /// <summary>
        /// Whether a pose track came back exactly as it went out.
        /// </summary>
        private static bool SamePoseTrack(UnitPoseTrack sent, UnitPoseTrack got, out string why)
        {
            why = string.Empty;

            if (got.Joints.Count != sent.Joints.Count)
            {
                why = $"arrived with {got.Joints.Count} joint names, not {sent.Joints.Count}";
                return false;
            }
            for (var j = 0; j < sent.Joints.Count; j++)
            {
                if (got.Joints[j] != sent.Joints[j])
                {
                    why = $"joint name {j} became '{got.Joints[j]}', not '{sent.Joints[j]}'";
                    return false;
                }
            }

            if (got.Keys.Count != sent.Keys.Count)
            {
                why = $"arrived with {got.Keys.Count} keys, not {sent.Keys.Count}";
                return false;
            }
            for (var k = 0; k < sent.Keys.Count; k++)
            {
                var a = sent.Keys[k];
                var b = got.Keys[k];
                if (a.Time != b.Time)
                {
                    why = $"key {k} is stamped {b.Time}, not {a.Time}";
                    return false;
                }
                if (a.SyncLeftEquipment != b.SyncLeftEquipment
                    || a.SyncRightEquipment != b.SyncRightEquipment)
                {
                    why = $"key {k} lost an equipment flag";
                    return false;
                }
                if (b.Joints.Count != a.Joints.Count)
                {
                    why = $"key {k} arrived with {b.Joints.Count} joints, not {a.Joints.Count}";
                    return false;
                }
                for (var j = 0; j < a.Joints.Count; j++)
                {
                    var from = a.Joints[j];
                    var to = b.Joints[j];
                    if (from.Position.X != to.Position.X || from.Position.Y != to.Position.Y
                        || from.Position.Z != to.Position.Z
                        || from.Rotation.X != to.Rotation.X || from.Rotation.Y != to.Rotation.Y
                        || from.Rotation.Z != to.Rotation.Z || from.Rotation.W != to.Rotation.W)
                    {
                        why = $"key {k} joint {j} changed crossing the wire";
                        return false;
                    }
                }
            }

            // M14 stage C. Order is asserted, not just membership: playback
            // reads "the newest ping" as "the last one that qualifies", which is
            // only true while the list stays ascending.
            if (got.Reactions.Count != sent.Reactions.Count)
            {
                why = $"arrived with {got.Reactions.Count} reaction pings, not {sent.Reactions.Count}";
                return false;
            }
            for (var r = 0; r < sent.Reactions.Count; r++)
            {
                if (got.Reactions[r] != sent.Reactions[r])
                {
                    why = $"reaction ping {r} is stamped {got.Reactions[r]}, not {sent.Reactions[r]}";
                    return false;
                }
            }

            if (got.Melees.Count != sent.Melees.Count)
            {
                why = $"arrived with {got.Melees.Count} melee swings, not {sent.Melees.Count}";
                return false;
            }
            for (var m = 0; m < sent.Melees.Count; m++)
            {
                var from = sent.Melees[m];
                var to = got.Melees[m];
                if (from.TimeStart != to.TimeStart || from.TimeEnd != to.TimeEnd)
                {
                    why = $"melee {m} changed its window crossing the wire";
                    return false;
                }
                if (from.PartUsed != to.PartUsed || from.ShockwaveKey != to.ShockwaveKey)
                {
                    why = $"melee {m} lost its preset crossing the wire";
                    return false;
                }

                // Both points, separately. They are the same type and adjacent
                // on the wire, so a transposition drags the shockwave backwards
                // along the swing while every count still agrees.
                if (from.PosStart.X != to.PosStart.X || from.PosStart.Y != to.PosStart.Y
                    || from.PosStart.Z != to.PosStart.Z)
                {
                    why = $"melee {m} moved its start point";
                    return false;
                }
                if (from.PosEnd.X != to.PosEnd.X || from.PosEnd.Y != to.PosEnd.Y
                    || from.PosEnd.Z != to.PosEnd.Z)
                {
                    why = $"melee {m} moved its end point";
                    return false;
                }
            }

            return true;
        }
    }
}
