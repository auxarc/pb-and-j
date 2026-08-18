using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One part the client must put into a settled state right now, rather than
    /// ramp towards during a window.
    /// </summary>
    /// <remarks>
    /// Carries no progress value because there are only two settled states and
    /// naming them beats encoding them as 1 and 0: a wrecked part is fully
    /// dissolved over zero integrity, and an un-wrecked one is a pristine part
    /// with no dissolve at all. The caller needs both halves of that, and a lone
    /// float would leave it inferring the integrity half.
    /// </remarks>
    public readonly struct DestructionDrive
    {
        public DestructionDrive(string? unit, string? socket, bool wrecked)
        {
            Unit = unit;
            Socket = socket;
            Wrecked = wrecked;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Unit { get; }

        /// <summary>The mount socket, which is how the visual is found.</summary>
        public string? Socket { get; }

        /// <summary>
        /// False when the host has <i>un</i>-wrecked the part and the client must
        /// undo its dissolve.
        /// </summary>
        /// <remarks>
        /// Reachable in ordinary play rather than theoretical:
        /// <c>CombatUnitRevive.cs:16-49</c> and <c>ScenarioUtility.cs:3993</c>
        /// both clear the flag on a host. An add-only design would leave a
        /// revived part dissolved for the rest of the fight, and — because the
        /// game's own setter no-ops on an unchanged value — would then be unable
        /// to show that part being destroyed a second time.
        /// </remarks>
        public bool Wrecked { get; }
    }

    /// <summary>
    /// One unit whose own wreck visual the client must play, or undo. M15 §3.1.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DestructionDrive"/> rather than a part drive
    /// with a null socket, because the two are answered by different calls on
    /// different objects and conflating them would put a "which kind is this"
    /// branch into every consumer. A unit wreck is a one-shot —
    /// <c>OnUnitDestruction</c> guards itself on <c>destroyedLast</c> — where a
    /// part is a ramp with a value.
    /// </remarks>
    public readonly struct UnitWreckDrive
    {
        public UnitWreckDrive(string? unit, bool wrecked)
        {
            Unit = unit;
            Wrecked = wrecked;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Unit { get; }

        /// <summary>
        /// False when the host has un-wrecked the unit, and the visual must be
        /// undone rather than played.
        /// </summary>
        /// <remarks>
        /// Expressible only because the game provides the inverse call:
        /// <c>IUnitVisualManager.OnUnitRevival()</c> clears the same
        /// <c>destroyedLast</c> flag <c>OnUnitDestruction</c> sets. Without it
        /// this direction would be unbuildable and a revived unit
        /// (<c>CombatUnitRevive.cs:28</c>) would stay a corpse on every client.
        /// </remarks>
        public bool Wrecked { get; }
    }

    /// <summary>
    /// What a snapshot or a finished window asks the client to put right.
    /// </summary>
    /// <remarks>
    /// Two lists rather than two calls, because they come from one walk over
    /// one snapshot and describe one instant. Letting a caller obtain the parts
    /// separately from the units is exactly how the two would be applied against
    /// different states — the same argument <see cref="KeyframeCapture"/> makes
    /// for keeping its window and its tracks together.
    /// </remarks>
    public sealed class DestructionUpdate
    {
        private static readonly DestructionDrive[] NoParts = new DestructionDrive[0];
        private static readonly UnitWreckDrive[] NoUnits = new UnitWreckDrive[0];

        /// <summary>Nothing to do — the overwhelmingly common answer.</summary>
        public static readonly DestructionUpdate Nothing = new DestructionUpdate(null, null);

        public DestructionUpdate(
            IReadOnlyList<DestructionDrive>? parts, IReadOnlyList<UnitWreckDrive>? units)
        {
            Parts = parts ?? NoParts;
            Units = units ?? NoUnits;
        }

        public IReadOnlyList<DestructionDrive> Parts { get; }

        public IReadOnlyList<UnitWreckDrive> Units { get; }

        public bool IsEmpty => Parts.Count == 0 && Units.Count == 0;
    }

    /// <summary>
    /// How far a wrecked part has dissolved at a given instant.
    /// </summary>
    /// <remarks>
    /// A transcription of the game's own replay arithmetic
    /// (<c>CombatReplayHelper.ApplyTimeToUnit:1288-1297</c>):
    /// <c>Clamp01((requested - destructionTime) / 0.5).EaseOutSine()</c>.
    /// <para>
    /// ⚠️ <c>EaseOutSine</c> itself is <b>not in the decompile</b> — the
    /// extension lives in an assembly that was not decompiled — so the source of
    /// truth for the curve is not in this tree. Three in-repo copies agree on
    /// <c>sin(x·π/2)</c> (<c>CIHoverable.cs:121-125</c> is the clearest), and
    /// that is what this reproduces. <c>Mathf.Sin</c> is
    /// <c>(float)Math.Sin((double)x)</c>, so the cast below is the same
    /// arithmetic rather than an approximation of it.
    /// </para>
    /// </remarks>
    public static class DestructionRamp
    {
        /// <summary>The ramp's length in seconds — the game's own <c>0.5f</c>.</summary>
        public const float Duration = 0.5f;

        /// <summary>
        /// How much a value must move before it is worth telling the visual.
        /// </summary>
        /// <remarks>
        /// The game's own threshold (<c>RoughlyEqual(..., 0.001f)</c>). It is not
        /// micro-optimisation: every accepted write runs a property-block refresh
        /// that iterates every renderer of every socket visual on the unit, so an
        /// unguarded per-frame drive would do that for every part of every unit
        /// on every frame of every window.
        /// </remarks>
        public const float ChangeEpsilon = 0.001f;

        /// <summary>
        /// The dissolve at <paramref name="now"/> for a part wrecked at
        /// <paramref name="destructionTime"/>.
        /// </summary>
        /// <remarks>
        /// Time-based rather than state-based, which is what removes the need for
        /// a "was this wrecked in the window I am playing" question anywhere else:
        /// a part wrecked in an earlier turn has a stamp far enough behind the
        /// cursor that it reads fully dissolved on the window's first frame, and
        /// one wrecked before the fight began (stamped <c>-100</c>) reads
        /// dissolved from any cursor at all.
        /// </remarks>
        public static float Progress(float destructionTime, float now)
        {
            var t = (now - destructionTime) / Duration;
            if (!(t > 0f))
            {
                // Written as a negated comparison so a NaN cursor settles at
                // zero instead of falling through to the sine. NaN reaches this
                // from a single dropped float on the wire, and a NaN in a shader
                // property block is how a unit turns inside out.
                return 0f;
            }
            if (t >= 1f)
            {
                return 1f;
            }
            return (float)Math.Sin(t * Math.PI / 2.0);
        }
    }

    /// <summary>
    /// Which of a client's parts are wrecked, and when it is allowed to say so.
    /// </summary>
    /// <remarks>
    /// The snapshot is the carrier and it always lands <i>before</i> the
    /// playback of the turn it describes. Applying it on arrival would therefore
    /// blow a limb off before the replay shows it being hit — worse than the
    /// missing visual it replaces, because it is actively wrong about causality
    /// rather than merely silent. So arrival splits the incoming set in two:
    /// <list type="bullet">
    /// <item>parts this client has <b>already been told about</b>, or that were
    /// wrecked before the fight began, settle at once — their moment is not in
    /// any window still to play;</item>
    /// <item>parts <b>new in this snapshot</b> are held, to be ramped by the
    /// window if one comes.</item>
    /// </list>
    /// <para>
    /// The held set needs no timer and no "did a window ever arrive" flag,
    /// because the next snapshot resolves it by construction: whatever this turn
    /// held is, by definition, already-known by the next one. So a turn whose
    /// keyframes never arrive — the mutual-destruction case, where the host
    /// legitimately sends none — converges one turn late instead of never, and
    /// <see cref="SettleWindow"/> closes it at once in the ordinary case.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> a second interval test. The crossing rule already
    /// exists as <see cref="ReplayAssetPlayback.CrossedDuring"/> and the caller
    /// uses it for the destruction burst; what could not be borrowed, and what
    /// this type is for, is the decision about which parts a window is even
    /// entitled to animate.
    /// </para>
    /// </remarks>
    public sealed class DestructionState
    {
        private static readonly PartDestruction[] NoParts = new PartDestruction[0];

        /// <summary>
        /// What the latest snapshot said about one unit.
        /// </summary>
        /// <remarks>
        /// The parts and the unit's own flag travel together because "have I
        /// heard about this unit before" is a question both halves ask, and two
        /// dictionaries could answer it differently after a partial update.
        /// </remarks>
        private sealed class Held
        {
            public Held(IReadOnlyList<PartDestruction> parts, bool wrecked)
            {
                Parts = parts;
                Wrecked = wrecked;
            }

            public IReadOnlyList<PartDestruction> Parts { get; set; }

            public bool Wrecked { get; set; }

            /// <summary>
            /// Whether the wreck visual has actually been shown yet.
            /// </summary>
            /// <remarks>
            /// Tracked separately from <see cref="Wrecked"/> because the two
            /// really do come apart, and losing the distinction loses a wreck
            /// outright: a unit held for a window whose keyframes never arrive
            /// is <i>known</i> wrecked by the next snapshot while nothing has
            /// ever drawn it. Without this the next snapshot would read "already
            /// known" and ask for nothing, for ever.
            /// </remarks>
            public bool WreckPlayed { get; set; }
        }

        private readonly Dictionary<string, Held> held = new Dictionary<string, Held>();
        private readonly Dictionary<string, float> driven = new Dictionary<string, float>();
        private readonly List<DestructionDrive> pendingParts = new List<DestructionDrive>();

        /// <summary>
        /// Units whose wreck is held for a window, and when each happened.
        /// </summary>
        /// <remarks>
        /// One structure rather than a list of drives beside a table of times.
        /// The list came first and was deleted: every held wreck is a wrecked:
        /// true drive, so the list was fully derivable from this — and holding
        /// both made a state expressible that nothing could ever produce, a time
        /// with no matching drive, which is an arm no test can reach and the
        /// coverage gate is right to refuse.
        /// </remarks>
        private readonly Dictionary<string, float> pendingWreckAt =
            new Dictionary<string, float>();

        /// <summary>
        /// How many parts this client believes are wrecked, across every unit.
        /// </summary>
        /// <remarks>
        /// Exists to be compared against the host's own count, and that
        /// comparison is the only mechanical check this feature has. The two
        /// machines hold the same fact in different places — the host in the
        /// ECS, a client only here, because a client deliberately never sets the
        /// <c>Wrecked</c> component — so neither the digest nor any existing
        /// counter can see them disagree.
        /// </remarks>
        public int Count
        {
            get
            {
                var total = 0;
                foreach (var entry in held.Values)
                {
                    total += entry.Parts.Count;
                }
                return total;
            }
        }

        /// <summary>How many units have at least one wrecked part.</summary>
        public int UnitCount
        {
            get
            {
                var units = 0;
                foreach (var entry in held.Values)
                {
                    if (entry.Parts.Count > 0)
                    {
                        units++;
                    }
                }
                return units;
            }
        }

        /// <summary>
        /// How many units this client believes the host has wrecked. M15 §3.1.
        /// </summary>
        /// <remarks>
        /// The unit-level counterpart of <see cref="Count"/>, and compared the
        /// same way: against the host's own ECS figure, because a client never
        /// sets the component and so cannot be asked the question any other way.
        /// </remarks>
        public int WreckedUnitCount
        {
            get
            {
                var units = 0;
                foreach (var entry in held.Values)
                {
                    if (entry.Wrecked)
                    {
                        units++;
                    }
                }
                return units;
            }
        }

        /// <summary>
        /// Whether the host has this unit wrecked, right now. M17 stage 1.
        /// </summary>
        /// <remarks>
        /// Asked by the client's playback wake, which must not hand a corpse
        /// back to its animator: on the host a wrecked mech is held down by its
        /// PuppetMaster being <c>Mode.Active</c>/<c>State.Dead</c>, and a client
        /// never sets <c>Wrecked</c>, so waking one re-poses it to a standing
        /// idle. The collapse really did play; it is the wake that undoes it.
        /// <para>
        /// Deliberately reads the <b>held</b> flag rather than
        /// <see cref="Held.WreckPlayed"/>. The question at wake time is "is this
        /// unit a corpse", not "did I draw the explosion" — a unit whose wreck
        /// is still waiting for its moment inside the window being closed is
        /// nonetheless a corpse by the time the window ends, and a unit settled
        /// with no window at all never had a moment to draw.
        /// </para>
        /// <para>
        /// ⚠️ <b>Answers correctly after <see cref="SettleWindow"/> and stops
        /// answering after <see cref="Clear"/>, and both are load-bearing.</b>
        /// A window's natural end settles and then stops, and combat end runs
        /// <c>Stop</c> — hence the wake — before <c>ClearDestruction</c>. If
        /// settling forgot the flag, every corpse would stand up at every turn
        /// boundary; if clearing did not, a view could be held down into the
        /// next fight. Both directions are covered by tests, because nothing
        /// else in the suite would notice either break.
        /// </para>
        /// </remarks>
        public bool IsUnitWrecked(string? unit)
        {
            return !string.IsNullOrEmpty(unit)
                && held.TryGetValue(unit!, out var record)
                && record.Wrecked;
        }

        /// <summary>
        /// Takes a snapshot's wrecked sets, and says what to settle immediately.
        /// </summary>
        /// <remarks>
        /// An empty or absent snapshot changes nothing at all, held set included.
        /// A client that hears nothing has been told nothing, and treating
        /// silence as "no part is wrecked any more" would un-dissolve the whole
        /// battlefield on the first turn something went wrong upstream.
        /// </remarks>
        public DestructionUpdate Receive(IReadOnlyList<UnitSnapshot>? units)
        {
            if (units == null || units.Count == 0)
            {
                return DestructionUpdate.Nothing;
            }

            // Last turn's held parts are in both the old and the new set by
            // construction, so they settle through the ordinary rule below and
            // need no special case here.
            pendingParts.Clear();
            pendingWreckAt.Clear();

            var settle = new List<DestructionDrive>();
            var settleUnits = new List<UnitWreckDrive>();
            for (var i = 0; i < units.Count; i++)
            {
                var name = units[i].Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var parts = units[i].WreckedParts;
                held.TryGetValue(name!, out var record);
                var previous = record != null ? record.Parts : null;
                var knownWrecked = record != null && record.Wrecked;

                if (record == null)
                {
                    record = new Held(parts, units[i].IsWrecked);
                    held[name!] = record;
                }
                else
                {
                    record.Parts = parts;
                    record.Wrecked = units[i].IsWrecked;
                }

                // The unit's own wreck, on the same split the parts take below,
                // and keyed on whether it has been SHOWN rather than on whether
                // it is known. Those come apart in the case this feature exists
                // for: a wreck held for a window whose keyframes never arrive is
                // known by the next snapshot and has still never been drawn.
                if (units[i].IsWrecked)
                {
                    if (!record.WreckPlayed)
                    {
                        // Settle at once when there is no window left that could
                        // show it: first sight of the unit, a stamp with no
                        // moment in this fight, or a wreck already held through
                        // a turn that never played. Otherwise hold it, so the
                        // explosion lands when the replay reaches the killing
                        // blow rather than before it.
                        if (previous == null || units[i].WreckedAt < 0f || knownWrecked)
                        {
                            settleUnits.Add(new UnitWreckDrive(name, true));
                            record.WreckPlayed = true;
                        }
                        else
                        {
                            pendingWreckAt[name!] = units[i].WreckedAt;
                        }
                    }
                }
                else if (knownWrecked)
                {
                    // Un-wrecked by the host. Clearing the played flag is what
                    // lets the unit be destroyed a second time and shown for it.
                    settleUnits.Add(new UnitWreckDrive(name, false));
                    record.WreckPlayed = false;
                }

                for (var p = 0; p < parts.Count; p++)
                {
                    var socket = parts[p].Socket;
                    if (string.IsNullOrEmpty(socket))
                    {
                        continue;
                    }

                    // A negative stamp is the spawn sentinel for a unit that
                    // arrived with the part already gone, so it has no moment in
                    // this fight to wait for. Without this arm, a client joining
                    // mid-battle would show pristine limbs on every such unit
                    // until a window happened to play.
                    if (parts[p].Time < 0f || Holds(previous, socket))
                    {
                        settle.Add(new DestructionDrive(name, socket, true));
                    }
                    else
                    {
                        pendingParts.Add(new DestructionDrive(name, socket, true));
                    }
                }

                if (previous == null)
                {
                    continue;
                }
                for (var p = 0; p < previous.Count; p++)
                {
                    var socket = previous[p].Socket;
                    if (string.IsNullOrEmpty(socket) || Holds(parts, socket))
                    {
                        continue;
                    }

                    // Gone from a live set, so the host un-wrecked it. Forgetting
                    // the last driven value matters as much as the drive itself:
                    // without it a part destroyed a second time would keep the
                    // stale entry, and the caller would never be told this was a
                    // first drive — leaving the dissolve painted over an
                    // integrity the client never zeroed.
                    settle.Add(new DestructionDrive(name, socket, false));
                    driven.Remove(Key(name!, socket!));
                }
            }
            return new DestructionUpdate(settle, settleUnits);
        }

        /// <summary>
        /// Settles everything a window was holding, because it has now played.
        /// </summary>
        /// <remarks>
        /// Called when playback reaches the end of its window, and it earns its
        /// place on the short tail: a part wrecked a tenth of a second before the
        /// window ends has ramped only a fifth of the way there, and without this
        /// it would sit half-dissolved through the whole of the next planning
        /// phase before the next snapshot tidied it.
        /// </remarks>
        public DestructionUpdate SettleWindow()
        {
            if (pendingParts.Count == 0 && pendingWreckAt.Count == 0)
            {
                return DestructionUpdate.Nothing;
            }

            var wrecks = new UnitWreckDrive[pendingWreckAt.Count];
            var at = 0;
            foreach (var unit in pendingWreckAt.Keys)
            {
                wrecks[at++] = new UnitWreckDrive(unit, true);
                MarkWreckPlayed(unit);
            }

            var settled = new DestructionUpdate(pendingParts.ToArray(), wrecks);
            pendingParts.Clear();
            pendingWreckAt.Clear();
            return settled;
        }

        /// <summary>
        /// Claims this unit's held wreck if the cursor just crossed its moment.
        /// M15 §3.1.
        /// </summary>
        /// <remarks>
        /// Takes rather than merely tests, and the two have to be one operation:
        /// a wreck is a one-shot, so a caller that asked and then acted would
        /// have to remember separately not to ask again, and
        /// <see cref="SettleWindow"/> would re-fire whatever it had already
        /// played.
        /// <para>
        /// The interval test is <see cref="ReplayAssetPlayback.CrossedDuring"/>
        /// on a zero-length span, not "is the cursor past it". A frame gap
        /// during playback is routinely longer than the events inside it, so a
        /// point test loses exactly the wrecks that happen between two frames.
        /// </para>
        /// </remarks>
        public bool TryTakeWreck(string? unit, float previousTime, float currentTime)
        {
            if (string.IsNullOrEmpty(unit)
                || !pendingWreckAt.TryGetValue(unit!, out var at)
                || !ReplayAssetPlayback.CrossedDuring(at, at, previousTime, currentTime))
            {
                return false;
            }

            pendingWreckAt.Remove(unit!);
            MarkWreckPlayed(unit!);
            return true;
        }

        /// <summary>
        /// This unit's whole wrecked set, for a window to ramp against.
        /// </summary>
        /// <remarks>
        /// The whole set, not the held part of it, because
        /// <see cref="DestructionRamp.Progress"/> already resolves the older
        /// entries to a flat 1 and re-driving them costs a comparison the change
        /// guard would refuse anyway. It also means a view rebuilt mid-window
        /// gets its older parts re-dissolved rather than left pristine.
        /// </remarks>
        public IReadOnlyList<PartDestruction> PartsFor(string? unit)
        {
            if (!string.IsNullOrEmpty(unit) && held.TryGetValue(unit!, out var record))
            {
                return record.Parts;
            }
            return NoParts;
        }

        /// <summary>
        /// Whether a progress value is worth handing to the visual, and whether
        /// this is the first time this part has been driven at all.
        /// </summary>
        /// <remarks>
        /// The second answer is the load-bearing one and it is not an
        /// optimisation. <c>OnSocketDestructionChange</c> re-applies the socket's
        /// <i>stored</i> integrity and defaults it to <c>1f</c> when nothing ever
        /// set it (<c>UnitVisualManager.cs:1755</c>) — so a client that drives
        /// the dissolve without first zeroing integrity, as the host does at
        /// wreck time (<c>EquipmentUtility.cs:3101</c>), renders a dissolve over
        /// a part that still reads pristine.
        /// </remarks>
        public bool ShouldDrive(string? unit, string? socket, float progress, out bool first)
        {
            first = false;
            if (string.IsNullOrEmpty(unit) || string.IsNullOrEmpty(socket))
            {
                return false;
            }

            var key = Key(unit!, socket!);
            if (driven.TryGetValue(key, out var last))
            {
                if (Math.Abs(last - progress) <= DestructionRamp.ChangeEpsilon)
                {
                    return false;
                }
            }
            else
            {
                first = true;
            }

            driven[key] = progress;
            return true;
        }

        /// <summary>
        /// Drops one part's last-driven value, so the next drive is a first one.
        /// </summary>
        /// <remarks>
        /// For a caller whose drive <b>threw</b> after
        /// <see cref="ShouldDrive"/> had already recorded the value it was about
        /// to send. Without this the table claims a value the visual never
        /// received, and at the end of a ramp — where the value stops changing —
        /// the guard would refuse every retry and the part would simply never
        /// dissolve. Mid-ramp it self-heals on the next frame, which is exactly
        /// what makes the resting case easy to miss.
        /// </remarks>
        public void Forget(string? unit, string? socket)
        {
            if (string.IsNullOrEmpty(unit) || string.IsNullOrEmpty(socket))
            {
                return;
            }
            driven.Remove(Key(unit!, socket!));
        }

        /// <summary>
        /// Forgets everything, for a session or a combat that has ended.
        /// </summary>
        /// <remarks>
        /// The driven table especially: it is keyed by unit name and socket, and
        /// the next fight reuses both. Carrying it across would let a part
        /// dissolved in the last battle suppress its own first drive in the next
        /// one, on a visual manager that has never been told anything.
        /// </remarks>
        public void Clear()
        {
            held.Clear();
            driven.Clear();
            pendingParts.Clear();
            pendingWreckAt.Clear();
        }

        private void MarkWreckPlayed(string unit)
        {
            if (held.TryGetValue(unit, out var record))
            {
                record.WreckPlayed = true;
            }
        }

        private static bool Holds(IReadOnlyList<PartDestruction>? parts, string? socket)
        {
            if (parts == null)
            {
                return false;
            }
            for (var i = 0; i < parts.Count; i++)
            {
                if (string.Equals(parts[i].Socket, socket, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // NUL rather than a printable separator. Both halves arrive as ordinary
        // game identifiers, but joining on anything they could legally contain
        // lets two different pairs collide into one key -- and a collision here
        // reads as "already driven", i.e. a part that silently never dissolves.
        private static string Key(string unit, string socket)
        {
            return unit + "\0" + socket;
        }
    }
}
