using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PBAndJ.Core;
using PBAndJ.Core.Net;
using PhantomBrigade.Data;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M12c: writes the combat checkpoint the session asked for, and measures what
    /// it cost.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="CombatShipGlue"/> and deliberately much
    /// simpler than it, because the two are asked at opposite moments.
    /// <c>ShipCombat</c> is asked in the same tick that raises
    /// <c>isScenarioIntroInProgress</c>, so it has to poll for a moment the game
    /// will accept. This is asked in the gap inside <c>HostSession.TryCommit</c>,
    /// where every <c>CanSave(false)</c> refusal is already known to be false —
    /// the barrier has filled, so the turn is not simulating; the fight is not
    /// resolved; nothing is loading. <b>So a refusal here is an anomaly to be
    /// logged, not a state to be waited out</b>, and this glue never polls,
    /// never re-arms and never retries.
    /// <para>
    /// <b>The write is synchronous and that is the whole point.</b> Passing
    /// <c>delayUntil</c> would take <c>DataManagerSave</c>'s
    /// <c>Co.StopAndClear(ref delayedSaveCoroutine)</c> / <c>DelayedSaveIE</c>
    /// branch, which cancels any pending vanilla save and defers ours past the
    /// instant that gives it its value. A checkpoint a few frames late holds a
    /// turn that has already begun simulating.
    /// </para>
    /// <para>
    /// <b>It also carries the milestone's one unmeasured number.</b>
    /// <c>DataManagerSave.SaveData</c> ends in an unconditional
    /// <c>RefreshSaveHeaders</c>, which clears and re-enumerates both the Normal
    /// and Internal save folders and YAML-parses every <c>metadata.yaml</c> in
    /// them — a cost that scales with the player's lifetime save count rather
    /// than with this save, paid once per Execute. Nobody has ever measured it.
    /// The stopwatch below is that measurement, and <c>pbj.checkpoint-stat</c> is
    /// how it is read back; the number decides the cadence in
    /// <see cref="HostSession"/>.
    /// </para>
    /// <para>
    /// ⚠️ <b>Two globals move on every write</b>, and this is per-turn where the
    /// mod previously paid it once per fight: <c>DataManagerSave</c> sets
    /// <c>SettingUtility.autoSaveTimer = 0f</c> and reassigns its own static
    /// <c>saveName</c>. The second is safe for loads — <c>LoadingStart</c> calls
    /// <c>SetSaveName(key)</c> before <c>LoadData</c>, so that global is a sink on
    /// the load path and never a source, which is why M11d's
    /// <see cref="LobbySaveWrites.NameForRead"/> cannot be steered by it. The
    /// first genuinely does move: the overworld autosave timer is reset once per
    /// turn, so the first timed autosave after a long fight comes later than it
    /// otherwise would.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class CheckpointGlue
    {
        /// <summary>
        /// What one tick of <see cref="Stopwatch"/> is worth, in milliseconds.
        /// </summary>
        /// <remarks>
        /// Printed by <see cref="CheckpointStat"/> beside every timing, because it
        /// is what separates the instrument's two zeros: a <c>0.0 ms</c> reading
        /// above this resolution is a genuinely fast save, and one at or below it
        /// is a clock that did not move. Without it, "0.0 ms" reads as a
        /// measurement when it may be the absence of one.
        /// </remarks>
        private static readonly double TickMilliseconds = 1000.0 / Stopwatch.Frequency;

        private static int attempts;
        private static int writes;
        private static int refusals;
        private static int faults;

        /// <summary>How many refusals named each reason, in first-seen order.</summary>
        private static readonly Dictionary<string, int> RefusalsByReason =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static double lastMilliseconds = -1.0;
        private static double worstMilliseconds = -1.0;
        private static double totalMilliseconds;
        private static int lastTurn = int.MinValue;

        /// <summary>
        /// Carries out one <c>WriteCheckpointEffect</c>. Never throws.
        /// </summary>
        /// <remarks>
        /// Swallowing the exception is not laziness. This runs inside
        /// <c>NetGlue.Pump</c>, which turns a throw into "networking stopped" for
        /// the rest of the process — so a save that fails would end the co-op
        /// session rather than skip a checkpoint. Nothing in the protocol waits on
        /// the write, so skipping one is survivable and losing the session is not.
        /// </remarks>
        internal static void Write(int turn)
        {
            attempts++;
            lastTurn = turn;

            if (!DataManagerSave.CanSave(false))
            {
                refusals++;
                var reason = CombatShipGlue.Blocker(out _);
                RefusalsByReason.TryGetValue(reason, out var seen);
                RefusalsByReason[reason] = seen + 1;

                // A warning, not a wait. Every refusal CanSave can give is meant
                // to be false at this instant, so one arriving means the moment
                // moved out from under M12c and the whole design needs re-reading
                // — which is worth a line loud enough to be found later.
                Debug.LogWarning("[pb-and-j] checkpoint REFUSED at turn " + turn + ": " + reason
                    + " — every CanSave refusal should already be false here, so this is an anomaly");
                return;
            }

            // ⭐ Arms the restore-side diff (R0 reading 2) with the plan this
            // checkpoint is about to serialise. Until this existed, only the
            // manual pbj.combat-save ever armed one, so loading a checkpoint
            // printed "no pre-save capture this session — diff skipped" and the
            // reading could not be taken at all.
            //
            // Three placement constraints, each load-bearing:
            //  * AFTER the CanSave gate — a refused turn writes no checkpoint,
            //    and a capture describing a save that does not exist is worse
            //    than no capture;
            //  * BEFORE the stopwatch — the stall number this class exists to
            //    carry is DoSave's cost and nothing else. Timing the snapshot
            //    walk with it would inflate the one figure the checkpoint
            //    cadence is decided on;
            //  * inside its own try — Write's contract is that it never throws
            //    (see this class's remarks). A throw here runs inside
            //    NetGlue.Pump and would end networking for the rest of the
            //    process rather than skip one diff.
            List<ActionSnapshot>? captured;
            try
            {
                captured = ActionDumpGlue.BuildSnapshots();
            }
            catch (Exception e)
            {
                captured = null;
                Debug.LogWarning("[pb-and-j] checkpoint diff NOT armed at turn " + turn + ": "
                    + e.GetType().Name + ": " + e.Message
                    + " — the checkpoint itself is still written below; only the restore diff is lost");
            }

            var watch = Stopwatch.StartNew();
            try
            {
                // previewScreenshot: false, for the reason CombatShipGlue already
                // gives — the preview coroutine hides and re-shows several views
                // to take its picture, and blinking the UI at every Execute is
                // worse than the cost of having no thumbnail on a slot nothing
                // shows in a grid.
                //
                // No delayUntil: see this class's remarks. Deferring the write
                // past this instant destroys the property it exists for.
                DataManagerSave.DoSave(
                    LobbySaveNames.CheckpointSlot, SaveLocation.Normal, null, -1, false);
            }
            catch (Exception e)
            {
                watch.Stop();
                faults++;
                Debug.LogWarning("[pb-and-j] checkpoint FAILED at turn " + turn + " after "
                    + Ms(watch.Elapsed.TotalMilliseconds) + " ms: "
                    + e.GetType().Name + ": " + e.Message);
                return;
            }

            watch.Stop();
            writes++;
            lastMilliseconds = watch.Elapsed.TotalMilliseconds;
            totalMilliseconds += lastMilliseconds;
            if (lastMilliseconds > worstMilliseconds)
            {
                worstMilliseconds = lastMilliseconds;
            }

            // Handed over only now, so an armed capture always names a
            // checkpoint that is actually on disk. The refusal and fault paths
            // above both return before this point.
            if (captured != null)
            {
                SaveLoadGlue.ArmRestoreDiff(
                    LobbySaveNames.CheckpointSlot,
                    captured,
                    "the automatic checkpoint write at turn "
                        + turn.ToString(CultureInfo.InvariantCulture));
            }

            Debug.Log("[pb-and-j] checkpoint: turn=" + turn
                + " ms=" + Ms(lastMilliseconds)
                + " writes=" + writes.ToString(CultureInfo.InvariantCulture)
                + " diff-armed=" + (captured != null ? "yes" : "NO"));
        }

        /// <summary>The <c>pbj.checkpoint-load</c> console command.</summary>
        /// <remarks>
        /// The counterpart nothing had: <c>SaveLoadGlue.CombatLoad</c> takes no
        /// arguments and loads <see cref="LobbySaveNames.ScenarioSlot"/>, so
        /// before this there was no command in the mod that could load the
        /// checkpoint at all — the per-turn save was writable, readable as a
        /// statistic, and unreachable as a save.
        /// <para>
        /// It goes through <c>DataHelperLoading.OnLoadingExternal</c>, exactly as
        /// <c>CombatLoad</c> does, so it inherits the same funnel:
        /// <c>TryLoading</c> pops the controller state to <c>mainmenu</c> and
        /// re-enters through <c>LoadingStart</c>. The key is already inside the
        /// <c>pbj_</c> prefix, so <c>SaveNamespacePatches</c>' redirect returns it
        /// unchanged (<see cref="LobbySaveWrites.NameForRead"/> leaves a
        /// multiplayer key alone).
        /// </para>
        /// <para>
        /// ⭐ <b>It warns instead of refusing, and that is deliberate.</b> A
        /// checkpoint from an earlier session is a legitimate thing to load, so
        /// blocking on <c>writes == 0</c> would be an instrument overruling its
        /// operator at the one moment the rig cannot argue back. What a zero
        /// really costs is the <em>reading</em>, not the load — so the zero is
        /// named in the reply, where it is impossible to miss, and the decision
        /// stays with whoever typed the command.
        /// </para>
        /// </remarks>
        public static string CheckpointLoad()
        {
            var slot = LobbySaveNames.CheckpointSlot;
            var warning = writes > 0
                ? string.Empty
                : "\n  ⚠ THIS PROCESS HAS WRITTEN NO CHECKPOINT (attempts " + attempts
                    + ", refusals " + refusals + ", faults " + faults + ")."
                    + "\n    Whatever loads now is an EARLIER session's checkpoint or nothing at all,"
                    + "\n    and the restore diff is armed by neither — it will print 'diff skipped'."
                    + "\n    Run pbj.checkpoint-stat, then commit a turn as host, then retry.";

            if (warning.Length > 0)
            {
                Debug.LogWarning("[pb-and-j] checkpoint load with no checkpoint written this session"
                    + warning);
            }

            DataHelperLoading.OnLoadingExternal(slot, SaveLocation.Normal);
            return "[pb-and-j] loading checkpoint '" + slot + "'..." + warning;
        }

        /// <summary>The <c>pbj.checkpoint-stat</c> console command.</summary>
        /// <remarks>
        /// ⭐ <b>Its zero prints its own alternative hypotheses.</b> "0 attempts"
        /// is the output of a machine that has committed no turn, of a client
        /// (which never writes a checkpoint, by design), and of a glue the effect
        /// never reached — three completely different situations wearing one
        /// number. Reporting the bare zero would let the third be read as either
        /// of the first two, which is exactly how a wiring failure survives a
        /// probe. So the zero says so itself rather than relying on whoever reads
        /// it remembering.
        /// <para>
        /// Same discipline for the timing: every reading is printed beside the
        /// stopwatch's own resolution, so a <c>0.0 ms</c> save cannot be confused
        /// with a stopwatch that never ran.
        /// </para>
        /// </remarks>
        public static string CheckpointStat()
        {
            var lines = new List<string>
            {
                "[pb-and-j] checkpoint stat — attempts " + attempts
                    + ", writes " + writes
                    + ", refusals " + refusals
                    + ", faults " + faults,
            };

            if (attempts == 0)
            {
                lines.Add("  ZERO ATTEMPTS IS AMBIGUOUS — it is the same number for all of:");
                lines.Add("    * no turn has been committed on this machine since it loaded;");
                lines.Add("    * this machine is a CLIENT — only the host checkpoints, by design;");
                lines.Add("    * the effect never reached this glue (WriteCheckpoint unwired).");
                lines.Add("  pbj.net-status separates the first two. For the third, commit a turn");
                lines.Add("  as host and watch for a 'checkpoint:' or 'checkpoint REFUSED' line.");
                return string.Join("\n", lines.ToArray());
            }

            lines.Add("  last: turn " + lastTurn + (writes > 0
                ? ", " + Ms(lastMilliseconds) + " ms"
                : ", never written"));

            if (writes > 0)
            {
                lines.Add("  worst " + Ms(worstMilliseconds) + " ms"
                    + " | mean " + Ms(totalMilliseconds / writes) + " ms"
                    + " over " + writes + " write(s)");
                lines.Add("  stopwatch resolution " + Ms(TickMilliseconds) + " ms/tick"
                    + " — a reading at or below that is a clock that did not move,"
                    + " not a save that took no time");
            }

            if (refusals > 0)
            {
                lines.Add("  refusals by reason:");
                foreach (var pair in RefusalsByReason)
                {
                    lines.Add("    " + pair.Value + "x  " + pair.Key);
                }
            }

            return string.Join("\n", lines.ToArray());
        }

        private static string Ms(double value)
        {
            // Four places rather than one: the stopwatch resolution is a fraction
            // of a microsecond on every platform this runs on, and rounding it to
            // 0.0 would destroy the one number that tells a fast save from a
            // stopped clock.
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }
}
