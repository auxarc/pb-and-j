using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One synchronised load: who was told to load, who has answered, and who has
    /// run out of time.
    /// </summary>
    /// <remarks>
    /// A third barrier rather than a reuse, for the reason <see cref="LobbyBarrier"/>
    /// is not <c>TurnBarrier</c>: a different question over a different set. The
    /// turn barrier collects orders, the lobby barrier collects agreement, and
    /// this collects arrivals — and unlike either of those, <b>a failure is an
    /// answer</b>. It is waiting for news, not for success.
    /// <para>
    /// It is keyed on the selection version the load fired at, and it keeps that
    /// version rather than reading the host's live one. Nothing stops the host
    /// re-selecting mid-load, and a report checked against the live selection
    /// would go stale and abandon a load that was going perfectly well.
    /// </para>
    /// <para>
    /// Deadlines are <b>not</b> minted by <see cref="Start"/>. A load fires from a
    /// message handler, where the session's clock may not have been stamped yet;
    /// a deadline of zero-plus-timeout judged against process uptime expires
    /// everybody on the first tick. <see cref="Seed"/> mints them from the clock
    /// the tick actually carries — the same seed-don't-judge shape the host's
    /// keepalive uses, for the same reason.
    /// </para>
    /// </remarks>
    public sealed class LoadBarrier
    {
        private static readonly int[] Nobody = new int[0];

        private readonly List<int> pending = new List<int>();
        private readonly Dictionary<int, LoadOutcome> answers = new Dictionary<int, LoadOutcome>();
        private readonly Dictionary<int, double> deadlines = new Dictionary<int, double>();

        /// <summary>Whether a load is running.</summary>
        public bool InFlight { get; private set; }

        /// <summary>The selection version this load was fired at.</summary>
        public int Version { get; private set; }

        /// <summary>Who has still not answered.</summary>
        public IReadOnlyList<int> Waiting => pending;

        /// <summary>
        /// True once nobody is left to hear from. False when no load is running,
        /// so a finished barrier does not read as a complete one.
        /// </summary>
        public bool IsComplete => InFlight && pending.Count == 0;

        /// <summary>Who actually got into the campaign.</summary>
        public IReadOnlyList<int> Loaded
        {
            get
            {
                List<int>? loaded = null;
                foreach (var pair in answers)
                {
                    if (pair.Value == LoadOutcome.Loaded)
                    {
                        (loaded ??= new List<int>()).Add(pair.Key);
                    }
                }
                loaded?.Sort();
                return (IReadOnlyList<int>?)loaded ?? Nobody;
            }
        }

        /// <summary>Begins a load, replacing anything already running.</summary>
        public void Start(int version, IReadOnlyList<int> participants)
        {
            pending.Clear();
            answers.Clear();
            deadlines.Clear();

            Version = version;
            InFlight = true;
            for (var i = 0; i < participants.Count; i++)
            {
                pending.Add(participants[i]);
            }
        }

        /// <summary>
        /// Records an answer. False if it was for a load nobody is waiting on.
        /// </summary>
        public bool Report(int participantId, int version, LoadOutcome outcome)
        {
            if (!InFlight || version != Version || !pending.Remove(participantId))
            {
                return false;
            }

            answers[participantId] = outcome;
            deadlines.Remove(participantId);
            return true;
        }

        /// <summary>What this participant said, or null if it has not.</summary>
        public LoadOutcome? OutcomeFor(int participantId)
        {
            return answers.TryGetValue(participantId, out var outcome) ? outcome : (LoadOutcome?)null;
        }

        /// <summary>
        /// Stops waiting for someone. What a timeout does, and what a disconnect
        /// mid-load does. False if they had already answered or were never in.
        /// </summary>
        public bool Drop(int participantId)
        {
            if (!InFlight || !pending.Remove(participantId))
            {
                return false;
            }
            deadlines.Remove(participantId);
            return true;
        }

        /// <summary>Ends the flight and forgets it.</summary>
        public void Finish()
        {
            InFlight = false;
            pending.Clear();
            answers.Clear();
            deadlines.Clear();
        }

        /// <summary>
        /// Gives everyone still waiting a deadline, from a clock that has
        /// actually ticked. Idempotent per participant.
        /// </summary>
        /// <remarks>
        /// Re-stamping would push the deadline forward on every tick — four times
        /// a second — and the timeout would never fire at all.
        /// </remarks>
        public void Seed(double nowSeconds)
        {
            if (!InFlight)
            {
                return;
            }

            for (var i = 0; i < pending.Count; i++)
            {
                if (!deadlines.ContainsKey(pending[i]))
                {
                    deadlines[pending[i]] = nowSeconds;
                }
            }
        }

        /// <summary>
        /// Who has been waited on for longer than <paramref name="timeoutSeconds"/>.
        /// </summary>
        /// <remarks>
        /// Returns rather than mutates, so the caller can log and compose effects
        /// before dropping anyone — the same shape <c>ExpireSilentHandshakes</c>
        /// uses, and for the same reason: mutating a collection while walking it
        /// is how that kind of loop goes wrong.
        /// </remarks>
        public IReadOnlyList<int> Expired(double nowSeconds, double timeoutSeconds)
        {
            if (!InFlight)
            {
                return Nobody;
            }

            List<int>? expired = null;
            for (var i = 0; i < pending.Count; i++)
            {
                if (deadlines.TryGetValue(pending[i], out var since)
                    && nowSeconds - since >= timeoutSeconds)
                {
                    (expired ??= new List<int>()).Add(pending[i]);
                }
            }
            return (IReadOnlyList<int>?)expired ?? Nobody;
        }
    }
}
