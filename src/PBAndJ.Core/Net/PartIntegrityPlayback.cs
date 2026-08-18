using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One part whose damage the client must write into its own ECS. M16.
    /// </summary>
    /// <remarks>
    /// Carries the values rather than a progress, because unlike a dissolve
    /// there is nothing to ramp: <c>integrityNormalized</c> and
    /// <c>barrierNormalized</c> are the numbers the host holds and the client
    /// must hold the same ones. The socket is the join key for the same reason
    /// <see cref="PartDestruction"/> uses it — an ECS id means nothing in
    /// another process.
    /// </remarks>
    public readonly struct PartIntegrityDrive
    {
        public PartIntegrityDrive(string? unit, string? socket, float integrity, float barrier)
        {
            Unit = unit;
            Socket = socket;
            Integrity = integrity;
            Barrier = barrier;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Unit { get; }

        /// <summary>The mount socket — <c>part.partParentUnit.socket</c>.</summary>
        public string? Socket { get; }

        public float Integrity { get; }

        public float Barrier { get; }
    }

    /// <summary>
    /// One unit's <c>unitFrameIntegrity</c>, including whether it exists at all.
    /// M16.
    /// </summary>
    /// <remarks>
    /// 🔑 <b><see cref="Present"/> is the whole point of this type, and absence
    /// is an instruction rather than a missing value.</b> A host in combat has no
    /// such component — <c>CombatScenarioSetupSystem.cs:466</c> distributes the
    /// scalar into the parts and removes it (<c>EquipmentUtility.cs:685-688</c>)
    /// — while a client, which enters combat by <i>loading</i> the scenario save,
    /// takes a different path entirely: <c>CombatScenarioSetupSystem.cs:60-64</c>
    /// returns early during loading, and <c>DataManagerSave.cs:2293-2301</c>
    /// installs the component unconditionally, defaulting to <c>1f</c>.
    /// <para>
    /// So the two machines disagree from combat entry, before anything crosses
    /// the wire, and a client can only be put right by <b>removing</b> what its
    /// own loader added. Sending the value alone would replace one wrong state
    /// with another: the field is serialized on presence
    /// (<c>DataHelperSaveSerialization.cs:481-487</c>) and read during combat as
    /// <c>has ? f : 1f</c> by <c>DataBlockUnitCheck.cs:126-128</c>,
    /// <c>DataHelperStats.cs:1141</c> and
    /// <c>CombatValidateUnitFrameIntegrity.cs:13</c>, so a phantom zero is not
    /// inert in either place.
    /// </para>
    /// </remarks>
    public readonly struct FrameIntegrityDrive
    {
        public FrameIntegrityDrive(string? unit, bool present, float integrity)
        {
            Unit = unit;
            Present = present;
            Integrity = integrity;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Unit { get; }

        /// <summary>Whether the host has the component at all.</summary>
        public bool Present { get; }

        /// <summary>Meaningless unless <see cref="Present"/>.</summary>
        public float Integrity { get; }
    }

    /// <summary>
    /// What a snapshot or a finished window asks the client to write. M16.
    /// </summary>
    /// <remarks>
    /// Two lists rather than two calls, for the reason
    /// <see cref="DestructionUpdate"/> gives: they come from one walk over one
    /// snapshot and describe one instant, and letting a caller obtain them
    /// separately is how they would end up applied against different states.
    /// </remarks>
    public sealed class PartIntegrityUpdate
    {
        private static readonly PartIntegrityDrive[] NoParts = new PartIntegrityDrive[0];
        private static readonly FrameIntegrityDrive[] NoFrames = new FrameIntegrityDrive[0];

        /// <summary>Nothing to do.</summary>
        public static readonly PartIntegrityUpdate Nothing = new PartIntegrityUpdate(null, null);

        public PartIntegrityUpdate(
            IReadOnlyList<PartIntegrityDrive>? parts,
            IReadOnlyList<FrameIntegrityDrive>? frames)
        {
            Parts = parts ?? NoParts;
            Frames = frames ?? NoFrames;
        }

        public IReadOnlyList<PartIntegrityDrive> Parts { get; }

        public IReadOnlyList<FrameIntegrityDrive> Frames { get; }

        public bool IsEmpty => Parts.Count == 0 && Frames.Count == 0;
    }

    /// <summary>
    /// Which of a client's parts hold which damage, and when it may say so. M16.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="DestructionState"/> and for the same reason:
    /// the snapshot always lands <i>before</i> the playback of the turn it
    /// describes (<c>HostSession.cs:1945-1951</c> sends TurnComplete, then the
    /// snapshot, then the keyframes, from one site over TCP), so applying it on
    /// arrival would drop a health bar before the replay showed the shot that
    /// did it.
    /// <para>
    /// ⭐ <b>Where it differs from <see cref="DestructionState"/> is the reason it
    /// needs no <c>WreckPlayed</c> equivalent.</b> A unit wreck is an event to
    /// fire once, so "known" and "shown" come apart and losing the distinction
    /// loses the wreck outright. Part integrity is a <i>value to re-assert</i>:
    /// a later write of the same or a newer value is always safe, so the state
    /// this type keeps is only about <b>when</b>, never about <b>whether</b>.
    /// </para>
    /// <para>
    /// There are three settle paths and the third is not optional.
    /// <see cref="SettleWindow"/> covers a window that played;
    /// <see cref="Receive"/> covers a window that never did, by settling the
    /// previous set before holding the new one; and the caller must call
    /// <see cref="SettleWindow"/> once more when combat ends, before
    /// <see cref="Clear"/>. Without that last one the final turn's damage is lost
    /// — deterministically in the mutual-destruction ending, where the host
    /// legitimately sends no keyframes at all (<c>HostSession.cs:1959</c>) and so
    /// no window ever plays, and there is no next snapshot to catch it.
    /// </para>
    /// </remarks>
    public sealed class PartIntegrityState
    {
        private readonly Dictionary<string, List<PartState>> held =
            new Dictionary<string, List<PartState>>();

        // Names, not part sets: the question this answers is "could a window
        // still show me this unit's damage arriving", and the answer is no only
        // the very first time the unit is described at all.
        private readonly HashSet<string> seen = new HashSet<string>();

        /// <summary>
        /// How many parts are waiting for a window, across every unit.
        /// </summary>
        /// <remarks>
        /// Reported beside the applied counters so a steady non-zero reading is
        /// legible: it means windows are not arriving, not that nothing is being
        /// damaged.
        /// </remarks>
        public int HeldCount
        {
            get
            {
                var total = 0;
                foreach (var entry in held.Values)
                {
                    total += entry.Count;
                }
                return total;
            }
        }

        /// <summary>
        /// Takes a snapshot's part state, and says what to write right now.
        /// </summary>
        /// <remarks>
        /// An absent or empty snapshot changes nothing, held set included — a
        /// client that hears nothing has been told nothing, and treating silence
        /// as "everything is pristine" would repair the whole battlefield.
        /// <para>
        /// 🔴 The previous held set is settled here, and it must be the
        /// <b>previous</b> one rather than the incoming one. Applying the newer
        /// values would show this turn's damage before this turn's window plays,
        /// which is the causality error the hold exists to prevent — and it is
        /// reachable on the ordinary mid-window commit path, not only in theory.
        /// The older set is safe by construction: whatever window could have
        /// shown it is over.
        /// </para>
        /// </remarks>
        public PartIntegrityUpdate Receive(IReadOnlyList<UnitSnapshot>? units)
        {
            if (units == null || units.Count == 0)
            {
                return PartIntegrityUpdate.Nothing;
            }

            var settle = new List<PartIntegrityDrive>();
            Drain(settle);

            var frames = new List<FrameIntegrityDrive>();
            for (var i = 0; i < units.Count; i++)
            {
                var name = units[i].Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // Never held. Nothing draws this during a fight — the in-combat
                // readers are data-driven checks and an effective-HP number — so
                // there is no causality to protect and holding it would be
                // machinery that buys nothing.
                frames.Add(new FrameIntegrityDrive(
                    name, units[i].HasFrameIntegrity, units[i].Integrity));

                var kept = Keep(units[i].Parts);
                if (seen.Add(name!))
                {
                    // First sight: a client joining mid-fight, or the opening
                    // snapshot. No window still to play could show this damage
                    // arriving, so waiting for one would leave the unit pristine
                    // indefinitely.
                    Add(settle, name, kept);
                    continue;
                }

                held[name!] = kept;
            }

            return new PartIntegrityUpdate(settle, frames);
        }

        /// <summary>
        /// Releases everything the window was holding, because it is over.
        /// </summary>
        /// <remarks>
        /// Called both when playback reaches the end of its window and once more
        /// when combat ends. The second call is what keeps the final turn's
        /// damage: an aborted or absent window never reaches the first, and at
        /// combat end there is no next snapshot to settle it instead.
        /// </remarks>
        public PartIntegrityUpdate SettleWindow()
        {
            var settle = new List<PartIntegrityDrive>();
            Drain(settle);
            if (settle.Count == 0)
            {
                return PartIntegrityUpdate.Nothing;
            }
            return new PartIntegrityUpdate(settle, null);
        }

        /// <summary>
        /// Forgets everything, for a combat or a session that has ended.
        /// </summary>
        /// <remarks>
        /// The seen-set especially. The next fight reuses unit names, and its
        /// first snapshot describes a battlefield this state has never watched —
        /// carrying the names across would hold that snapshot for a window
        /// instead of settling it, leaving every unit pristine until the second
        /// turn.
        /// </remarks>
        public void Clear()
        {
            held.Clear();
            seen.Clear();
        }

        private void Drain(List<PartIntegrityDrive> into)
        {
            foreach (var entry in held)
            {
                Add(into, entry.Key, entry.Value);
            }
            held.Clear();
        }

        private static void Add(
            List<PartIntegrityDrive> into, string? unit, List<PartState> parts)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                into.Add(new PartIntegrityDrive(
                    unit, parts[i].Socket, parts[i].Integrity, parts[i].Barrier));
            }
        }

        // Filtered on the way in rather than on the way out, so HeldCount never
        // reports work the settle cannot produce.
        private static List<PartState> Keep(IReadOnlyList<PartState> parts)
        {
            var kept = new List<PartState>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                if (!string.IsNullOrEmpty(parts[i].Socket))
                {
                    kept.Add(parts[i]);
                }
            }
            return kept;
        }
    }
}
