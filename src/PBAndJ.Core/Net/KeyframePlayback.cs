using System;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Two pose keys and the blend between them.
    /// </summary>
    /// <remarks>
    /// Both ends are the same key when the request falls outside the track,
    /// so a caller never has to ask whether it is clamped — it interpolates
    /// unconditionally and gets the endpoint.
    /// </remarks>
    public readonly struct PoseSpan
    {
        public PoseSpan(PoseKey from, PoseKey to, float t)
        {
            From = from;
            To = to;
            T = t;
        }

        public PoseKey From { get; }
        public PoseKey To { get; }

        /// <summary>0 at <see cref="From"/>, 1 at <see cref="To"/>.</summary>
        public float T { get; }

        /// <summary>
        /// The equipment flags to honour for this instant.
        /// </summary>
        /// <remarks>
        /// The <b>upper</b> key's, because that is the one the game reads
        /// (<c>ApplyTimeToUnit</c> tests the keyframe it scanned forward to, not
        /// its predecessor). Pinning a weapon to the palm a tenth of a second
        /// early or late is not visible; disagreeing with the host about
        /// <i>whether</i> it is pinned is.
        /// </remarks>
        public bool SyncLeftEquipment => To.SyncLeftEquipment;

        /// <inheritdoc cref="SyncLeftEquipment"/>
        public bool SyncRightEquipment => To.SyncRightEquipment;
    }

    /// <summary>
    /// Reads a <see cref="UnitTrack"/> at an arbitrary time — the whole of
    /// keyframe playback that is arithmetic rather than engine calls.
    /// </summary>
    /// <remarks>
    /// Deliberately pure and here rather than in the glue: it is the one part of
    /// M6 that can be wrong in a way no in-game eyeball would catch, so it lives
    /// under the coverage gate and is shared verbatim by the game and the
    /// harness.
    /// <para>
    /// The semantics mirror the game's own scrubber (<c>ApplyTimeToUnit</c>):
    /// linear on position, normalised-lerp on rotation, and a skipped
    /// zero-length segment. Matching it matters because the host's replay UI is
    /// the reference for what the turn "looked like".
    /// </para>
    /// </remarks>
    public static class KeyframePlayback
    {
        private static readonly Vec3 Origin = new Vec3(0f, 0f, 0f);
        private static readonly Vec4 Identity = new Vec4(0f, 0f, 0f, 1f);

        /// <summary>
        /// Where the unit was at <paramref name="time"/>, or false if the track
        /// carries nothing to place it by.
        /// </summary>
        /// <remarks>
        /// Clamps rather than extrapolating outside the track. A track that ends
        /// early means the unit stopped being recorded — destroyed, hidden — not
        /// that it carried on moving.
        /// </remarks>
        public static bool TrySample(UnitTrack? track, float time, out Vec3 position, out Vec4 rotation)
        {
            position = Origin;
            rotation = Identity;

            if (track == null || track.Transforms.Count == 0)
            {
                return false;
            }

            var keys = track.Transforms;
            var last = keys.Count - 1;

            if (time <= keys[0].Time)
            {
                position = keys[0].Position;
                rotation = Sanitise(keys[0].Rotation);
                return true;
            }
            if (time >= keys[last].Time)
            {
                position = keys[last].Position;
                rotation = Sanitise(keys[last].Rotation);
                return true;
            }

            // Terminates without a bounds check: the clamp above established
            // keys[last].Time > time, so some index satisfies this.
            var index = 1;
            while (keys[index].Time < time)
            {
                index++;
            }

            var from = keys[index - 1];
            var to = keys[index];

            // The span is strictly positive and needs no zero guard, which is
            // worth stating because the recorder really does stamp two keys with
            // the same time — its own final sample and the one capture appends
            // land together at execution end. It cannot bite here: the loop only
            // stops where keys[index].Time >= time, and it only got there past a
            // key with a smaller time (or, at index 1, past the clamp that
            // established time > keys[0].Time). Equal-timed neighbours are
            // therefore always both on the same side of the requested time.
            var t = (time - from.Time) / (to.Time - from.Time);
            position = new Vec3(
                Lerp(from.Position.X, to.Position.X, t),
                Lerp(from.Position.Y, to.Position.Y, t),
                Lerp(from.Position.Z, to.Position.Z, t));
            rotation = LerpRotation(from.Rotation, to.Rotation, t);
            return true;
        }

        /// <summary>
        /// The two pose keys straddling <paramref name="time"/>, and how far
        /// between them it falls.
        /// </summary>
        /// <remarks>
        /// A bracket rather than a sampled pose, which is the whole reason this
        /// signature looks different from <see cref="TrySample"/>. A pose is
        /// dozens of joints and the caller writes them straight into engine
        /// Transforms; returning an array would allocate one per unit per frame
        /// for a value nothing keeps. So the arithmetic that can be wrong lives
        /// here and the copying lives in the glue.
        /// <para>
        /// Clamped at both ends, exactly as the transform track is, and for the
        /// same reason: a track that stops early means the unit stopped being
        /// recorded, not that it kept moving.
        /// </para>
        /// </remarks>
        public static bool TryBracket(UnitPoseTrack? track, float time, out PoseSpan span)
        {
            span = default;
            if (track == null || track.Keys.Count == 0)
            {
                return false;
            }

            var keys = track.Keys;
            var last = keys.Count - 1;

            if (time <= keys[0].Time)
            {
                span = new PoseSpan(keys[0], keys[0], 0f);
                return true;
            }
            if (time >= keys[last].Time)
            {
                span = new PoseSpan(keys[last], keys[last], 0f);
                return true;
            }

            // Terminates unchecked for the reason TrySample's loop does: the
            // clamp above established keys[last].Time > time.
            var index = 1;
            while (keys[index].Time < time)
            {
                index++;
            }

            var from = keys[index - 1];
            var to = keys[index];

            // Strictly positive by the same argument as TrySample's span, with
            // one addition: the host drops a head-equal pair before sending
            // (PoseTracks.TryPrepare), so even the one pair that argument does
            // not cover cannot arrive equal.
            span = new PoseSpan(from, to, (time - from.Time) / (to.Time - from.Time));
            return true;
        }

        /// <summary>
        /// Interpolates one joint of a bracketed pose.
        /// </summary>
        /// <remarks>
        /// Position lerp and normalised-rotation lerp, matching the game's own
        /// <c>PoseAnimationMode.Lerp</c> default — the host's replay UI is the
        /// reference for what the turn looked like, so a different curve here
        /// would be a different animation.
        /// </remarks>
        public static void SampleJoint(
            PoseSpan span, int joint, out Vec3 position, out Vec4 rotation)
        {
            var from = span.From.Joints[joint];
            var to = span.To.Joints[joint];

            position = new Vec3(
                Lerp(from.Position.X, to.Position.X, span.T),
                Lerp(from.Position.Y, to.Position.Y, span.T),
                Lerp(from.Position.Z, to.Position.Z, span.T));
            rotation = LerpRotation(from.Rotation, to.Rotation, span.T);
        }

        private static float Lerp(float from, float to, float t)
        {
            return from + ((to - from) * t);
        }

        /// <summary>
        /// Normalised lerp along the shorter arc, which is what Unity's
        /// <c>Quaternion.Lerp</c> does.
        /// </summary>
        /// <remarks>
        /// The sign flip is not cosmetic: q and -q are the same rotation, so
        /// without it a unit whose recorded rotation crosses that boundary spins
        /// the long way round on the client and looks nothing like what the host
        /// did.
        /// </remarks>
        private static Vec4 LerpRotation(Vec4 from, Vec4 to, float t)
        {
            var dot = (from.X * to.X) + (from.Y * to.Y) + (from.Z * to.Z) + (from.W * to.W);
            var sign = dot < 0f ? -1f : 1f;

            return Normalise(new Vec4(
                Lerp(from.X, to.X * sign, t),
                Lerp(from.Y, to.Y * sign, t),
                Lerp(from.Z, to.Z * sign, t),
                Lerp(from.W, to.W * sign, t)));
        }

        /// <summary>
        /// A recorded rotation, passed through untouched unless it is degenerate.
        /// </summary>
        /// <remarks>
        /// Landing exactly on a key must return that key's bits, not a
        /// re-normalised approximation of them. The whole M6 invariant is that
        /// the last key of a track equals the snapshot for the same unit, and a
        /// recorded quaternion is rarely of exactly unit length — normalising it
        /// here would move the endpoint by a few ulps and break that equality for
        /// no gain, since the value is whatever Unity itself was holding.
        /// </remarks>
        private static Vec4 Sanitise(Vec4 value)
        {
            var lengthSquared =
                (value.X * value.X) + (value.Y * value.Y) +
                (value.Z * value.Z) + (value.W * value.W);
            return lengthSquared < 1e-12f ? Identity : value;
        }

        /// <summary>
        /// A rotation of unit length, or identity if the input has no length to
        /// scale.
        /// </summary>
        /// <remarks>
        /// An all-zero quaternion is not a rotation and should never reach us,
        /// but it is exactly what a default-constructed or corrupt
        /// <see cref="Vec4"/> looks like. Dividing by its magnitude would put NaN
        /// into somebody's transform, which Unity propagates silently until a
        /// unit vanishes.
        /// </remarks>
        private static Vec4 Normalise(Vec4 value)
        {
            var lengthSquared =
                (value.X * value.X) + (value.Y * value.Y) +
                (value.Z * value.Z) + (value.W * value.W);
            if (lengthSquared < 1e-12f)
            {
                return Identity;
            }

            var scale = 1f / (float)Math.Sqrt(lengthSquared);
            return new Vec4(value.X * scale, value.Y * scale, value.Z * scale, value.W * scale);
        }
    }
}
