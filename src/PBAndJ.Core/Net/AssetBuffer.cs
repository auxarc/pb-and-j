using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Collects one turn's asset parts on a client until the transform
    /// keyframes arrive and end the burst.
    /// </summary>
    /// <remarks>
    /// <see cref="PoseBuffer"/>'s sibling, deliberately down to the shape of
    /// its guards, because it is solving the same problem against the same
    /// stream — and the two are separate types rather than one generic one
    /// because what they reassemble differs in kind: a pose buffer collects one
    /// track per part, this one concatenates three parallel collections. A
    /// shared generic would have to be parameterised on the merge, which is the
    /// only interesting line in either.
    /// <para>
    /// An <b>instance</b>, held by the session, never a static: a rejoin builds
    /// a fresh session and a buffer that outlived one would offer the previous
    /// session's effects to the next.
    /// </para>
    /// <para>
    /// Everything here compares <b>message labels against message labels</b>.
    /// The session's own turn counter is not a usable reference point — the
    /// host's <c>TurnComplete</c> travels ahead of these parts and advances it,
    /// so parts labelled turn T invariably arrive while the session already
    /// reads T+1. A buffer checking against session state would reject every
    /// part of every turn, and the only symptom would be a client on which
    /// nothing ever shoots.
    /// </para>
    /// </remarks>
    public sealed class AssetBuffer
    {
        private readonly List<StandaloneAssetTrack> standalone = new List<StandaloneAssetTrack>();
        private readonly List<ProjectileAssetTrack> projectiles = new List<ProjectileAssetTrack>();
        private readonly List<BeamAssetTrack> beams = new List<BeamAssetTrack>();
        private bool[] seen = new bool[0];
        private int turn = -1;
        private int partCount;
        private int received;

        /// <summary>The turn being accumulated, or -1 when empty.</summary>
        public int Turn => turn;

        /// <summary>Parts held so far, for the log line that explains a loss.</summary>
        public int PartsHeld => received;

        /// <summary>Parts the host said to expect.</summary>
        public int PartsExpected => partCount;

        /// <summary>
        /// Takes one part. A part for a different turn starts the buffer over.
        /// </summary>
        /// <remarks>
        /// Starting over on <i>any</i> label change rather than only on a higher
        /// one, for the reason <see cref="PoseBuffer.Accept"/> does: a client
        /// that rejoins mid-combat can be told about a turn it has already seen,
        /// and treating the newer message as stale would leave it accumulating
        /// a turn that will never complete.
        /// </remarks>
        public void Accept(ReplayAssetsMessage message)
        {
            if (message.Turn != turn)
            {
                Clear();
                turn = message.Turn;
                partCount = message.PartCount;
                seen = new bool[partCount];
            }

            if (message.PartIndex < 0 || message.PartIndex >= seen.Length)
            {
                return;
            }
            if (seen[message.PartIndex])
            {
                return;
            }

            seen[message.PartIndex] = true;
            received++;

            // Concatenation in the order the parts arrived, which is the order
            // the host packed them in — the two halves of one rule that lives
            // in ReplayAssetParts.Split. Duplicate ids across parts are not
            // possible within a turn; across turns they are expected, and that
            // is playback's problem rather than this one's.
            standalone.AddRange(message.Assets.Standalone);
            projectiles.AddRange(message.Assets.Projectiles);
            beams.AddRange(message.Assets.Beams);
        }

        /// <summary>
        /// The complete asset set for <paramref name="forTurn"/>, if it is
        /// complete. Empties the buffer either way.
        /// </summary>
        /// <remarks>
        /// All or nothing, and that is not the same policy
        /// <see cref="ReplayAssetParts"/> applies per track at capture. A
        /// missing effect the host chose not to send is one effect; a missing
        /// <i>part</i> is an arbitrary slice of a turn, and playing three
        /// quarters of a turn's projectiles would show shots that stop in mid
        /// air with nothing to say why.
        /// </remarks>
        public AssetCapture Take(int forTurn)
        {
            // A complete-but-empty turn is not special-cased into
            // AssetCapture.None. It reassembles into a capture whose three
            // collections are empty, which IsEmpty already answers for — and
            // one way of saying "nothing" is enough.
            var complete = turn == forTurn && received == partCount;
            var taken = complete
                ? new AssetCapture(
                    standalone.ToArray(), projectiles.ToArray(), beams.ToArray())
                : AssetCapture.None;
            Clear();
            return taken;
        }

        /// <summary>Forgets everything. Playback stopping means these are moot.</summary>
        public void Clear()
        {
            standalone.Clear();
            projectiles.Clear();
            beams.Clear();
            seen = new bool[0];
            turn = -1;
            partCount = 0;
            received = 0;
        }
    }
}
