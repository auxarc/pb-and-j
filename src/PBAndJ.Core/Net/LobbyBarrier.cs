using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Which save the lobby is gathered around, and how many times that has
    /// changed.
    /// </summary>
    /// <remarks>
    /// <see cref="Version"/> is the lobby's analogue of the turn number: it is
    /// what makes a stale ready recognisable. Change the save and every ready
    /// must clear, or a peer that agreed to save A is counted as agreeing to
    /// save B.
    /// <para>
    /// The key and digest are supplied from outside — the session never reads a
    /// disk, the same way it never reads a clock. A digest of <c>null</c> is
    /// normal for a save this machine has not hashed.
    /// </para>
    /// </remarks>
    public readonly struct LobbySelection
    {
        /// <summary>No save chosen yet. Version 0, matching a fresh barrier.</summary>
        public static readonly LobbySelection None = new LobbySelection(0, null, null);

        public LobbySelection(int version, string? saveKey, string? saveDigest)
        {
            Version = version;
            SaveKey = saveKey;
            SaveDigest = saveDigest;
        }

        public int Version { get; }

        /// <summary>The save name, or null when nothing is chosen.</summary>
        public string? SaveKey { get; }

        public string? SaveDigest { get; }

        /// <summary>Whether there is a save to be ready for.</summary>
        public bool HasSave => !string.IsNullOrEmpty(SaveKey);

        /// <summary>The next selection, one version on.</summary>
        /// <remarks>
        /// Re-choosing the same key still advances, so every ready clears. One
        /// path with no equality branch, and clearing is the safe direction: the
        /// cost is that someone re-clicks Ready, and the alternative is starting
        /// a save a peer never confirmed.
        /// </remarks>
        public LobbySelection Next(string? saveKey, string? saveDigest) =>
            new LobbySelection(Version + 1, saveKey, saveDigest);
    }

    /// <summary>
    /// Tracks who in the lobby has agreed to load the selected save. The host
    /// may proceed once every member — itself included — has.
    /// </summary>
    /// <remarks>
    /// A second barrier alongside <see cref="TurnBarrier"/> rather than a second
    /// instance of it. The membership differs — the turn barrier's participants
    /// come from combat assignment, the lobby's from the roster — and so does
    /// the counter: this one advances when the <em>save</em> changes, not when
    /// the turn does. A <c>TurnBarrier</c> whose <c>Turn</c> held a save
    /// selection version would be a lie in the field name.
    /// <para>
    /// It also has to answer <see cref="ReadyParticipants"/>, which the turn
    /// barrier never needs: a lobby screen renders who is ready, a combat
    /// barrier only counts.
    /// </para>
    /// <para>
    /// <see cref="ReadyOutcome"/> is reused verbatim, but note that
    /// <see cref="ReadyOutcome.NeedsResync"/> means something different here —
    /// see <see cref="SetReady"/>.
    /// </para>
    /// </remarks>
    public sealed class LobbyBarrier
    {
        private readonly List<int> participants = new List<int>();
        private readonly HashSet<int> ready = new HashSet<int>();

        public LobbyBarrier(int selection)
        {
            Selection = selection;
        }

        /// <summary>The selection version currently being collected.</summary>
        public int Selection { get; private set; }

        /// <summary>Everyone in the lobby, in the order they joined.</summary>
        public IReadOnlyList<int> Participants => participants;

        public int ParticipantCount => participants.Count;

        public int ReadyCount => ready.Count;

        /// <summary>
        /// Who has agreed, in roster order rather than the order they agreed in.
        /// </summary>
        /// <remarks>
        /// Roster order because this is rendered beside the roster; ready order
        /// would have the list reshuffle itself as people click. Derived rather
        /// than stored, so it cannot disagree with <see cref="ReadyCount"/>.
        /// </remarks>
        public IReadOnlyList<int> ReadyParticipants
        {
            get
            {
                var result = new List<int>(ready.Count);
                for (var i = 0; i < participants.Count; i++)
                {
                    if (ready.Contains(participants[i]))
                    {
                        result.Add(participants[i]);
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// True when every member is ready. False for an empty lobby — there is
        /// nothing to agree to.
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
        /// Drops a member and their readiness.
        /// </summary>
        /// <remarks>
        /// This can <em>satisfy</em> the barrier, exactly as
        /// <see cref="TurnBarrier.RemoveParticipant"/> can — so a caller that
        /// only checks satisfaction after a ready will miss the case where the
        /// last unready peer simply left.
        /// </remarks>
        public bool RemoveParticipant(int participantId)
        {
            ready.Remove(participantId);
            return participants.Remove(participantId);
        }

        public bool IsReady(int participantId) => ready.Contains(participantId);

        /// <summary>Records agreement to a specific selection.</summary>
        /// <remarks>
        /// <see cref="ReadyOutcome.NeedsResync"/> does <em>not</em> have the
        /// meaning it carries on <see cref="TurnBarrier"/>. There it is a normal
        /// occurrence, because <c>CombatForceExecution</c> can advance the host
        /// outside the barrier. Here there is no such path: only the host mints a
        /// selection version, it never rewinds, and the stream is ordered — so a
        /// peer claiming a version we have not reached is misbehaving, not
        /// merely behind. It is still an outcome rather than a disconnect,
        /// because refusing to answer is cheaper than kicking someone over what
        /// might be our own bug.
        /// </remarks>
        public ReadyOutcome SetReady(int participantId, int selection)
        {
            if (!participants.Contains(participantId))
            {
                return ReadyOutcome.UnknownParticipant;
            }
            if (selection < Selection)
            {
                return ReadyOutcome.Stale;
            }
            if (selection > Selection)
            {
                return ReadyOutcome.NeedsResync;
            }

            ready.Add(participantId);
            return ReadyOutcome.Accepted;
        }

        /// <summary>Withdraws an agreement. Returns false if they had not agreed.</summary>
        public bool Unready(int participantId) => ready.Remove(participantId);

        /// <summary>Moves to a new selection, clearing every agreement.</summary>
        public void AdvanceTo(int selection)
        {
            Selection = selection;
            ready.Clear();
        }
    }
}
