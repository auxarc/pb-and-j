using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Counts the combat edge <see cref="PbjRuntime"/> actually observed, so
    /// "did a mid-combat reload drive <c>InCombat</c> false→true" is a number
    /// rather than a guess.
    /// </summary>
    /// <remarks>
    /// The reading R0 needs, and the one M12c stage D forks on: if the reload
    /// drives the edge, a resumed host re-ships the fight for free through the
    /// path it already has; if it does not, stage D has to add an explicit exit
    /// before the load.
    /// <para>
    /// 🔑 <b>Nobody can take this reading by hand.</b> The edge is observed once
    /// per pump, the pump is a <c>Heartbeat.Update</c> postfix, and a save load
    /// blocks the main thread for seconds — so a transition can happen and be
    /// gone between two console commands. It has to be counted while it
    /// happens.
    /// </para>
    /// <para>
    /// ⚠️ <b>What this watches is the runtime's own memory, not a copy of its
    /// rule.</b> The obvious implementation — re-evaluate
    /// <c>IsGameState("combat") &amp;&amp; combat.hasCurrentTurn</c> in the
    /// sampler — would be a second, independent predicate that agrees with the
    /// real one until the day somebody changes
    /// <c>CombatGameBridge.InCombat</c>, after which the probe reports on a rule
    /// the session no longer uses and cannot be told apart from one that works.
    /// So the sampler reads <c>PbjRuntime.lastInCombat</c>, which is the value
    /// the edge is computed from, and the live bridge value beside it.
    /// </para>
    /// <para>
    /// ⚠️ <b>Reading this is not free of side effects, and the run sheet does
    /// not say so.</b> On a host the exit edge runs
    /// <c>HostSession.HandleCombatExited</c> — state to Lobby, assignments
    /// dropped, barrier to −1, <c>CombatEndMessage</c> broadcast — and the enter
    /// edge runs <c>HandleCombatEntered</c>, which raises a
    /// <c>ShipCombatEffect</c> and writes a fresh scenario save. Taking this
    /// reading therefore <b>ends and restarts the session's idea of the
    /// fight</b>. It belongs after every other reading that needed the fight to
    /// still be running.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class CombatEdgeProbeGlue
    {
        private const string Tag = "[pb-and-j] combat-edge";

        /// <summary>Transitions kept for the report, newest last.</summary>
        private const int TrailLimit = 16;

        private static bool armed;
        private static float deadline;

        private static int frames;
        private static int tickMoves;
        private static int enters;
        private static int exits;

        private static bool haveLastSeen;
        private static bool lastSeen;
        private static double lastTickSeconds;
        private static bool haveLastTick;

        private static readonly List<string> Trail = new List<string>();

        private static FieldInfo? runtimeField;
        private static FieldInfo? inCombatField;
        private static FieldInfo? tickField;
        private static FieldInfo? bridgeField;
        private static FieldInfo? stoppedField;

        /// <summary>
        /// Arms the sampler for <paramref name="seconds"/> and clears the
        /// counters. Pass 0 to disarm.
        /// </summary>
        /// <remarks>
        /// ⭐ <b>The vacuity guard is on the instrument and it fires here, not
        /// later.</b> Every private member this probe reads is resolved before
        /// the watch starts, and a single missing one <b>refuses to arm</b> and
        /// names it. The alternative — resolving lazily and printing whatever
        /// came back — produces a run of zeros that is indistinguishable from
        /// "the edge never fired", which is the answer this whole reading
        /// exists to establish. A probe that cannot see must say so before the
        /// rig is booked, not after.
        /// </remarks>
        [Command("pbj.combat-edge-watch", "R0/M12c-D: arm the combat-edge sampler for N seconds (0 disarms)")]
        public static string CombatEdgeWatch(int seconds)
        {
            if (seconds <= 0)
            {
                armed = false;
                var off = Tag + " disarmed | frames=" + frames.ToString(CultureInfo.InvariantCulture)
                    + " enters=" + enters.ToString(CultureInfo.InvariantCulture)
                    + " exits=" + exits.ToString(CultureInfo.InvariantCulture)
                    + " — counters kept; pbj.combat-edge still reports them";
                Debug.Log(off);
                return off;
            }

            if (seconds > 3600)
            {
                return Tag + " seconds must be 1..3600";
            }

            var missing = Resolve();
            if (missing.Count > 0)
            {
                var refusal = new StringBuilder(Tag)
                    .Append(" REFUSING TO ARM — cannot see: ");
                for (var i = 0; i < missing.Count; i++)
                {
                    if (i > 0)
                    {
                        refusal.Append(", ");
                    }
                    refusal.Append(missing[i]);
                }

                refusal.Append(". These are private members of PbjRuntime and NetGlue that this probe")
                    .Append(" reads by reflection; one of them has been renamed or removed. Arming")
                    .Append(" anyway would print zeros that read exactly like 'the edge never fired'.");

                var line = refusal.ToString();
                Debug.LogWarning(line);
                return line;
            }

            armed = true;
            deadline = Time.realtimeSinceStartup + seconds;
            frames = 0;
            tickMoves = 0;
            enters = 0;
            exits = 0;
            haveLastSeen = false;
            haveLastTick = false;
            Trail.Clear();

            var armedLine = Tag + " armed for " + seconds.ToString(CultureInfo.InvariantCulture)
                + "s | all five private members resolved | counters cleared"
                + " — do the load now, then run pbj.combat-edge";
            Debug.Log(armedLine);
            return armedLine;
        }

        /// <summary>
        /// Reports the counters, and says in words which of the five things a
        /// run of zeros would mean.
        /// </summary>
        /// <remarks>
        /// ⭐ <b>What a zero means.</b> Five separate states produce
        /// <c>enters=0 exits=0</c> and only one of them is the answer:
        /// <list type="bullet">
        /// <item><c>frames=0</c> — the sampler never ran. Either it was never
        /// armed, or the <c>Heartbeat.Update</c> postfix did not apply, in which
        /// case the mod's own pump is dead too and nothing else on the rig
        /// works either.</item>
        /// <item><c>runtime=null</c> — there is no session, so no edge is being
        /// computed at all. This is not a reading about combat.</item>
        /// <item><c>stopped=True</c> — the runtime exists and
        /// <c>Pump</c> returns on its first line. Same effect, different
        /// cause.</item>
        /// <item><c>tickMoves=0</c> with a live runtime — the pump did not run
        /// across the window. <c>lastTickSeconds</c> is advanced by
        /// <c>ObserveTick</c> up to four times a second from inside
        /// <c>Pump</c>, so it not moving is the pump not running, independently
        /// of anything to do with combat.</item>
        /// <item><c>tickMoves&gt;0</c>, <c>lastInCombat=True</c> throughout —
        /// <b>the pump ran and <c>InCombat</c> never went false.</b> This is the
        /// real answer, and it is "no, the reload does not drive the
        /// edge".</item>
        /// </list>
        /// <c>liveInCombat</c> is printed beside <c>lastInCombat</c> for the
        /// sixth case, which is neither: the two disagreeing means the bridge
        /// has already changed and the runtime has not pumped since.
        /// </remarks>
        [Command("pbj.combat-edge", "R0/M12c-D: combat-edge counters and what a zero in them means")]
        public static string CombatEdge()
        {
            var runtime = ReadRuntime();
            var sb = new StringBuilder(Tag);

            sb.Append(" | armed=").Append(armed)
                .Append(" frames=").Append(frames)
                .Append(" hasSession=").Append(NetGlue.HasSession)
                .Append(" runtime=").Append(runtime == null ? "null" : "present");

            if (runtime != null)
            {
                sb.Append(" stopped=").Append(Describe(stoppedField, runtime))
                    .Append(" lastInCombat=").Append(Describe(inCombatField, runtime))
                    .Append(" liveInCombat=").Append(DescribeLiveInCombat(runtime));
            }

            sb.Append(" | tickMoves=").Append(tickMoves)
                .Append(" exits=").Append(exits)
                .Append(" enters=").Append(enters)
                .Append(" trail=").Append(Trail.Count == 0 ? "<none>" : string.Join(",", Trail.ToArray()));

            sb.Append(" | ").Append(Verdict(runtime));

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <remarks>
        /// The sentence a reader would otherwise have to reconstruct from the
        /// counters, written by the thing that holds them. Sighting 14 in the
        /// vacuous-guards record is an instrument whose zero printed its own
        /// alternative hypothesis and so fooled nobody; this is that, made a
        /// field of the output rather than a habit of the reader.
        /// </remarks>
        private static string Verdict(PbjRuntime? runtime)
        {
            if (!armed && frames == 0)
            {
                return "VERDICT: never armed — run pbj.combat-edge-watch <seconds> BEFORE the load."
                    + " This is not a reading.";
            }

            if (frames == 0)
            {
                return "VERDICT: armed but zero frames — the Heartbeat.Update postfix is not running."
                    + " The mod's own pump shares that hook, so nothing else works either.";
            }

            if (runtime == null)
            {
                return "VERDICT: no session — nothing is computing a combat edge. Start or join one"
                    + " first; a zero here says nothing about combat.";
            }

            if (tickMoves == 0)
            {
                return "VERDICT: the pump did not run across this window (lastTickSeconds never moved)."
                    + " Check stopped= above. The edge CANNOT have fired, so its zero is not evidence.";
            }

            if (exits == 0 && enters == 0)
            {
                return "VERDICT: the pump ran and the edge did not move — InCombat held at "
                    + Describe(inCombatField, runtime)
                    + " for the whole window. THIS is the real negative answer.";
            }

            if (exits > 0 && enters > 0)
            {
                return "VERDICT: the edge fired both ways — see trail= for the order."
                    + " An X before an E across a load is the exit-then-enter M12c stage D wants.";
            }

            return "VERDICT: a one-sided edge (exits=" + exits.ToString(CultureInfo.InvariantCulture)
                + " enters=" + enters.ToString(CultureInfo.InvariantCulture)
                + "). The window closed mid-transition, or the load left InCombat where it landed.";
        }

        /// <summary>One sample, taken once per frame while armed.</summary>
        /// <remarks>
        /// Sampling per frame rather than per pump on purpose: the pump is
        /// behind a session guard and a killed session would stop the sampler
        /// too, turning "the pump died" into "the probe died" — the two
        /// readings this is here to separate.
        /// </remarks>
        internal static void Tick()
        {
            if (!armed)
            {
                return;
            }

            if (Time.realtimeSinceStartup > deadline)
            {
                armed = false;
                Debug.Log(Tag + " window closed — run pbj.combat-edge to read it");
                return;
            }

            frames++;

            var runtime = ReadRuntime();
            if (runtime == null)
            {
                return;
            }

            if (tickField?.GetValue(runtime) is double tick)
            {
                if (haveLastTick && Math.Abs(tick - lastTickSeconds) > double.Epsilon)
                {
                    tickMoves++;
                }
                lastTickSeconds = tick;
                haveLastTick = true;
            }

            if (!(inCombatField?.GetValue(runtime) is bool inCombat))
            {
                return;
            }

            if (!haveLastSeen)
            {
                haveLastSeen = true;
                lastSeen = inCombat;
                return;
            }

            if (inCombat == lastSeen)
            {
                return;
            }

            lastSeen = inCombat;
            if (inCombat)
            {
                enters++;
            }
            else
            {
                exits++;
            }

            if (Trail.Count < TrailLimit)
            {
                Trail.Add((inCombat ? "E@" : "X@") + frames.ToString(CultureInfo.InvariantCulture));
            }
            else if (Trail.Count == TrailLimit)
            {
                Trail.Add("...");
            }
        }

        /// <remarks>
        /// Resolved once and cached, but re-resolved on every arm so that a
        /// rebuild which renamed one of them is caught at arming rather than
        /// carried forward from a stale handle.
        /// </remarks>
        private static List<string> Resolve()
        {
            runtimeField = typeof(NetGlue).GetField(
                "runtime", BindingFlags.Static | BindingFlags.NonPublic);
            inCombatField = typeof(PbjRuntime).GetField(
                "lastInCombat", BindingFlags.Instance | BindingFlags.NonPublic);
            tickField = typeof(PbjRuntime).GetField(
                "lastTickSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
            bridgeField = typeof(PbjRuntime).GetField(
                "bridge", BindingFlags.Instance | BindingFlags.NonPublic);
            stoppedField = typeof(PbjRuntime).GetField(
                "stopped", BindingFlags.Instance | BindingFlags.NonPublic);

            var missing = new List<string>();
            if (runtimeField == null)
            {
                missing.Add("NetGlue.runtime");
            }
            if (inCombatField == null)
            {
                missing.Add("PbjRuntime.lastInCombat");
            }
            if (tickField == null)
            {
                missing.Add("PbjRuntime.lastTickSeconds");
            }
            if (bridgeField == null)
            {
                missing.Add("PbjRuntime.bridge");
            }
            if (stoppedField == null)
            {
                missing.Add("PbjRuntime.stopped");
            }

            return missing;
        }

        private static PbjRuntime? ReadRuntime()
        {
            if (runtimeField == null)
            {
                Resolve();
            }

            return runtimeField?.GetValue(null) as PbjRuntime;
        }

        private static string Describe(FieldInfo? field, object target)
        {
            if (field == null)
            {
                return "<unresolved>";
            }

            var value = field.GetValue(target);
            return value == null ? "<null>" : (value.ToString() ?? "<null>");
        }

        /// <remarks>
        /// The bridge the runtime holds, not one this probe made: a fresh
        /// <c>CombatGameBridge</c> would answer the same question today and stop
        /// being the session's answer the moment the session takes a different
        /// bridge.
        /// </remarks>
        private static string DescribeLiveInCombat(PbjRuntime runtime)
        {
            if (bridgeField == null)
            {
                return "<unresolved>";
            }

            return bridgeField.GetValue(runtime) is IPbjGameBridge live
                ? live.InCombat.ToString()
                : "<no bridge>";
        }

        /// <summary>
        /// Hands these commands to Quantum Console.
        /// </summary>
        /// <remarks>
        /// ⚠️ The <c>[Command]</c> attribute alone does nothing in this
        /// assembly — QC's own scan does not reach it, so registration is
        /// explicit and the attribute is documentation.
        /// </remarks>
        internal static void RegisterConsoleCommands()
        {
            Add(nameof(CombatEdge), "pbj.combat-edge");
            Add(nameof(CombatEdgeWatch), "pbj.combat-edge-watch", typeof(int));
        }

        private static void Add(string methodName, string command, params Type[] signature)
        {
            var method = typeof(CombatEdgeProbeGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public,
                null, signature, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }

    // The sampler's hook, and its own type because the [HarmonyPatch] attribute
    // above the declaration is what makes it apply at all.
    //
    // Deliberately a second postfix on Heartbeat.Update rather than a line added
    // to Patch_Heartbeat_Update: that method is in the NetGlue split family and
    // this probe is meant to be liftable in one file when R0 and R1 are read and
    // it is swept.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "Update")]
    internal static class Patch_Heartbeat_Update_CombatEdge
    {
        private static void Postfix()
        {
            CombatEdgeProbeGlue.Tick();
        }
    }
}
