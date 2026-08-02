using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>What the barrier did with a Ready.</summary>
    public enum ReadyOutcome
    {
        /// <summary>Recorded.</summary>
        Accepted = 0,

        /// <summary>For an already-committed turn — ignore it.</summary>
        Stale = 1,

        /// <summary>Peer is behind the host; tell it the real turn.</summary>
        NeedsResync = 2,

        /// <summary>Not a participant — ignore it.</summary>
        UnknownParticipant = 3,
    }

    /// <summary>
    /// Tracks who has submitted orders for the current turn. The host commits
    /// once every participant — itself included — is ready.
    /// </summary>
    /// <remarks>
    /// Participant ids are peer ids, with the host as <see cref="PbjPeerRegistry.HostPeerId"/>.
    /// <para>
    /// A Ready for a <em>future</em> turn is <see cref="ReadyOutcome.NeedsResync"/>
    /// rather than a protocol violation: <c>CombatForceExecution</c> lets scenario
    /// content advance the host's turn without going through the barrier, so a
    /// peer can be legitimately behind through no fault of its own.
    /// </para>
    /// </remarks>
    public sealed class TurnBarrier
    {
        private readonly List<int> participants = new List<int>();
        private readonly HashSet<int> ready = new HashSet<int>();

        public TurnBarrier(int turn)
        {
            Turn = turn;
        }

        /// <summary>The turn currently being collected.</summary>
        public int Turn { get; private set; }

        public int ParticipantCount => participants.Count;

        public int ReadyCount => ready.Count;

        /// <summary>
        /// True when every participant is ready. False for an empty session —
        /// there is nothing to commit.
        /// </summary>
        public bool IsSatisfied => participants.Count > 0 && ready.Count >= participants.Count;

        public void AddParticipant(int participantId)
        {
            if (!participants.Contains(participantId))
            {
                participants.Add(participantId);
            }
        }

        /// <summary>
        /// Drops a participant and their readiness. The barrier may become
        /// satisfied as a result — a departing peer must never wedge the session.
        /// </summary>
        public bool RemoveParticipant(int participantId)
        {
            ready.Remove(participantId);
            return participants.Remove(participantId);
        }

        public bool IsReady(int participantId) => ready.Contains(participantId);

        public ReadyOutcome SetReady(int participantId, int turn)
        {
            if (!participants.Contains(participantId))
            {
                return ReadyOutcome.UnknownParticipant;
            }
            if (turn < Turn)
            {
                return ReadyOutcome.Stale;
            }
            if (turn > Turn)
            {
                return ReadyOutcome.NeedsResync;
            }

            ready.Add(participantId);
            return ReadyOutcome.Accepted;
        }

        /// <summary>Withdraws a readiness. Returns false if they were not ready.</summary>
        public bool Unready(int participantId) => ready.Remove(participantId);

        /// <summary>Moves to a new turn, clearing every readiness.</summary>
        public void AdvanceTo(int turn)
        {
            Turn = turn;
            ready.Clear();
        }
    }
}
