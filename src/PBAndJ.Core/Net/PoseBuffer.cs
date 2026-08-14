using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Collects one turn's pose parts on a client until the transform keyframes
    /// arrive and end the burst.
    /// </summary>
    /// <remarks>
    /// An <b>instance</b>, held by the session that receives the parts, never a
    /// static. The mod's own <c>KeyframePlayer</c> keeps its cursor in a static
    /// and that is exactly the trap: a rejoin builds a fresh session, and a
    /// buffer that outlived it would offer the previous session's poses to the
    /// next one.
    /// <para>
    /// Everything here compares <b>message labels against message labels</b>.
    /// The session's own turn counter is not a usable reference point: the
    /// host's <c>TurnComplete</c> travels ahead of these parts and advances it,
    /// so parts labelled turn T invariably arrive while the session already
    /// reads T+1. A buffer that checked against session state would reject every
    /// part of every turn, and the only symptom would be that poses never
    /// appear.
    /// </para>
    /// </remarks>
    public sealed class PoseBuffer
    {
        private static readonly UnitPoseTrack[] NoTracks = new UnitPoseTrack[0];

        private readonly List<UnitPoseTrack> tracks = new List<UnitPoseTrack>();
        private bool[] seen = new bool[0];
        private int turn = -1;
        private int partCount;
        private int received;

        /// <summary>The turn being accumulated, or -1 when empty.</summary>
        public int Turn => turn;

        /// <summary>Parts held so far, for the log line that explains a fallback.</summary>
        public int PartsHeld => received;

        /// <summary>Parts the host said to expect.</summary>
        public int PartsExpected => partCount;

        /// <summary>
        /// Takes one part. A part for a different turn starts the buffer over.
        /// </summary>
        /// <remarks>
        /// Starting over on <i>any</i> label change, rather than only on a
        /// higher one, is deliberate. A client that rejoins mid-combat can be
        /// told about a turn number it has already seen, and treating the newer
        /// message as stale would leave it accumulating a turn that will never
        /// complete. The newest thing the host said is the thing to believe.
        /// </remarks>
        public void Accept(PosesMessage message)
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
            if (message.Track != null)
            {
                tracks.Add(message.Track);
            }
        }

        /// <summary>
        /// The complete pose set for <paramref name="forTurn"/>, if it is
        /// complete. Empties the buffer either way.
        /// </summary>
        /// <remarks>
        /// Emptying on failure as well as on success is the whole lifecycle:
        /// the terminator for a turn is the last thing that will ever refer to
        /// it, so anything still held afterwards is unreachable by definition
        /// and would only be able to confuse a later turn.
        /// </remarks>
        public IReadOnlyList<UnitPoseTrack> Take(int forTurn)
        {
            var complete = turn == forTurn && received == partCount;
            var taken = complete && tracks.Count > 0
                ? tracks.ToArray()
                : NoTracks;
            Clear();
            return taken;
        }

        /// <summary>Forgets everything. Playback stopping means these are moot.</summary>
        public void Clear()
        {
            tracks.Clear();
            seen = new bool[0];
            turn = -1;
            partCount = 0;
            received = 0;
        }
    }
}
