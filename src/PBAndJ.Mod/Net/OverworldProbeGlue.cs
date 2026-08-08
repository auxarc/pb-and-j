using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using HarmonyLib;
using PhantomBrigade;
using PhantomBrigade.Data;
using PhantomBrigade.Overworld;
using PhantomBrigade.Overworld.Systems;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // THROWAWAY, like LoadProbeGlue before it and the four probes deleted with
    // M10. This one answers M12's shape: what a client's overworld does on its
    // own, whether the host can drive that client's base from outside the sim,
    // and what has to be suppressed so a client cannot drive it back.
    // Delete it once every finding is in docs/notes/overworld-recon.md.
    //
    // Three readings moved the question before this file existed, which is why
    // it exists rather than a fourth careful read:
    //
    //   * "The overworld runs a continuous clock" (docs/design/campaign-coop.md
    //     :32) is wrong as written. OverworldTimeUtility:35-52 zeroes
    //     simulationTimeScale when !isBaseMoving, and
    //     OverworldMovementDetectionSystem:26-27 sets that from
    //     hasPath || hasPathfindingRequest. But it is not simply "the clock runs
    //     while travelling" either: RefreshTimeScale has no per-frame caller,
    //     CIViewOverworldRoot.ForceTimeScale:291-307 writes the scale directly,
    //     a time skip is a STATIONARY base with a fast clock, and
    //     timeUnlockWithoutSimulation frees Time.timeScale with the sim frozen.
    //     Sample() is what settles which of these a client actually sits in.
    //
    //   * The renderer is not the component you would guess. The overworld
    //     PositionLinkSystem collects on PositionDetectedLast, NOT Position, and
    //     renders positionDetectedLast.v (PositionLinkSystem:17,32-35). The
    //     bridge that copies position -> positionDetectedLast is
    //     OverworldRangeSystem, reactive on AnyOf(SimulationTime, Position).
    //
    //   * Those two live in DIFFERENT system stacks. GameController:177-183
    //     puts PositionLinkSystem in OverworldSystemsPermanent (always active)
    //     and OverworldRangeSystem in OverworldSystems, which runs only in game
    //     state "overworld" — while the management screens are game state
    //     "basecrawler". So "the client watches the host drive while using the
    //     workshop" is asking for two game states at once. Probe() prints the
    //     whole state stack for exactly this reason.
    //
    // Console return values do NOT reach Player.log — Quantum Console renders
    // them in its own view — so everything worth keeping is Debug.Log'd.
    [ExcludeFromCodeCoverage]
    internal static class OverworldProbeGlue
    {
        private const string Tag = "[pb-and-j] ow-probe";

        private static MonoBehaviour? host;
        private static bool sampling;

        /// <summary>
        /// Measurement 4's gate. The control-entry patches below are always
        /// installed (ModLink calls Harmony.PatchAll on the assembly) but stay
        /// silent until pbj.ow-watch turns them on, so an ordinary session is
        /// not spammed by a probe nobody asked for.
        /// </summary>
        internal static bool Watching;

        internal static void SetCoroutineHost(MonoBehaviour behaviour)
        {
            host = behaviour;
        }

        // --- pbj.ow-probe: one read-only shot, safe from any game state ---

        public static string OverworldProbe()
        {
            var report = new StringBuilder();
            report.Append(Tag).Append('\n');

            Section(report, "game state stack", ProbeGameStates);
            Section(report, "clock", ProbeClock);
            Section(report, "player base", ProbePlayerBase);
            Section(report, "saveability", ProbeSaveability);
            Section(report, "management views", ProbeManagementViews);

            Debug.Log(report.ToString());
            return Tag + " written to the log";
        }

        /// <remarks>One section failing must not cost the others.</remarks>
        private static void Section(StringBuilder report, string name, Action<StringBuilder> body)
        {
            report.Append("--- ").Append(name).Append(" ---\n");
            try
            {
                body(report);
            }
            catch (Exception e)
            {
                report.Append("  FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }
        }

        /// <remarks>
        /// The whole stack, not just IsGameState("overworld"). Which stacks are
        /// live decides whether OverworldSystems (the clock, ranges, movement)
        /// is ticking at all — and the management screens run in a different
        /// state from the map. This is the measurement the design doc is
        /// missing entirely.
        /// </remarks>
        private static void ProbeGameStates(StringBuilder report)
        {
            var controller = GameController.GetInstance();
            if (controller == null)
            {
                report.Append("  GameController.GetInstance() is NULL\n");
                return;
            }

            report.Append("  stack (bottom first):");
            foreach (var state in controller.m_stateStack)
            {
                report.Append(' ').Append(state?.Name ?? "<null>");
            }

            report.Append('\n');
            report.Append("  top: ").Append(controller.TopState?.Name ?? "<null>").Append('\n');
            report.Append("  IsGameState overworld: ").Append(IDUtility.IsGameState("overworld"))
                  .Append(" | basecrawler: ").Append(IDUtility.IsGameState("basecrawler"))
                  .Append(" | combat: ").Append(IDUtility.IsGameState("combat"))
                  .Append(" | mainmenu: ").Append(IDUtility.IsGameState("mainmenu")).Append('\n');
        }

        private static void ProbeClock(StringBuilder report)
        {
            var overworld = Contexts.sharedInstance.overworld;

            report.Append("  simulationTimeScale: ")
                  .Append(overworld.hasSimulationTimeScale ? overworld.simulationTimeScale.f.ToString("F4") : "<absent>")
                  .Append(" | backUp: ")
                  .Append(overworld.hasSimulationTimeScaleBackUp ? overworld.simulationTimeScaleBackUp.f.ToString("F4") : "<absent>")
                  .Append('\n');
            report.Append("  simulationTime: ")
                  .Append(overworld.hasSimulationTime ? overworld.simulationTime.f.ToString("F4") : "<absent>")
                  .Append(" | delta: ")
                  .Append(overworld.hasSimulationDeltaTime ? overworld.simulationDeltaTime.f.ToString("F6") : "<absent>")
                  .Append('\n');
            report.Append("  isBaseMoving: ").Append(overworld.isBaseMoving)
                  .Append(" | hasSimulationLockCountdown: ").Append(overworld.hasSimulationLockCountdown)
                  .Append('\n');

            // Time.timeScale is worth reading but not worth trusting on a single
            // sample: OverworldSimulationTimeSystem's REACTIVE half (:178-187)
            // sets Time.timeScale from simulationTime rather than from the
            // scale, so every ReplaceSimulationTimeScale flicks it toward 2
            // until the next per-frame Execute corrects it. A game bug, ours to
            // sample around rather than fix.
            report.Append("  Time.timeScale: ").Append(Time.timeScale.ToString("F4"))
                  .Append(" | unscaledDeltaTime: ").Append(Time.unscaledDeltaTime.ToString("F6"))
                  .Append('\n');
            report.Append("  timeUnlockWithoutSimulation: ")
                  .Append(OverworldSimulationTimeSystem.timeUnlockWithoutSimulation).Append('\n');

            // The autosave is the counter-example to "a stopped clock is a
            // stopped game": OverworldTimedAutosaveSystem:15-25 accumulates on
            // unscaledDeltaTime, and its zero-scale early return demands
            // IsGameState("overworld") — so in basecrawler it keeps counting and
            // fires real saves. Divergence source AND noise in measurement 2.
            report.Append("  autoSaveAtInterval: ").Append(DataShortcuts.overworld.autoSaveAtInterval)
                  .Append(" | autoSaveTimer: ").Append(SettingUtility.autoSaveTimer.ToString("F2"))
                  .Append(" | limit: ").Append(DataShortcuts.overworld.autoSaveTimerSeconds.ToString("F0"))
                  .Append('\n');
        }

        private static void ProbePlayerBase(StringBuilder report)
        {
            var b = IDUtility.playerBaseOverworld;
            if (b == null)
            {
                report.Append("  playerBaseOverworld is NULL (no campaign loaded?)\n");
                return;
            }

            report.Append("  id: ").Append(b.hasId ? b.id.id.ToString() : "<absent>").Append('\n');
            report.Append("  position: ").Append(b.hasPosition ? b.position.v.ToString("F2") : "<absent>").Append('\n');
            report.Append("  positionTarget: ").Append(b.hasPositionTarget ? b.positionTarget.v.ToString("F2") : "<absent>").Append('\n');

            // The rendered value. If this lags position after a write, the
            // OverworldRangeSystem bridge is not running — which is the whole
            // question for a client sitting in basecrawler.
            report.Append("  positionDetectedLast: ")
                  .Append(b.hasPositionDetectedLast ? b.positionDetectedLast.v.ToString("F2") : "<absent>")
                  .Append('\n');
            report.Append("  hasOverworldView: ").Append(b.hasOverworldView)
                  .Append(" | isPositionUnchecked: ").Append(b.isPositionUnchecked)
                  .Append('\n');
            report.Append("  hasPath: ").Append(b.hasPath)
                  .Append(" | hasPathfindingRequest: ").Append(b.hasPathfindingRequest)
                  .Append(" | isDeployed: ").Append(b.isDeployed)
                  .Append(" | isCloaked: ").Append(b.isCloaked)
                  .Append('\n');
        }

        private static void ProbeSaveability(StringBuilder report)
        {
            // Measurement 2 runs the game's own `save` command on both machines,
            // so knowing when CanSave is false matters: DataManagerSave:94-149
            // refuses during a time skip (hasSimulationLockCountdown || cloaked),
            // debriefing, the tutorial, a load and combat.
            report.Append("  DataManagerSave.CanSave(): ").Append(DataManagerSave.CanSave()).Append('\n');
            report.Append("  multiplayer campaign: ").Append(MultiplayerCampaign.Active)
                  .Append(" | save key: ").Append(MultiplayerCampaign.SaveKey ?? "<none>")
                  .Append('\n');
        }

        /// <remarks>
        /// Measurement 5. Null means never constructed; non-null but not entered
        /// means dormant and reachable, which is what the M11 load probe found
        /// for the load screen's own singletons.
        /// </remarks>
        private static void ProbeManagementViews(StringBuilder report)
        {
            // The first pass watched CIViewBaseWorkshopV2 alone and concluded the
            // management UI was never opened. It is the CRAFTING workshop — the
            // mech loadout screen everyone actually means is CIViewBaseLoadout,
            // and it was open the whole time. Naming one screen "the management
            // UI" is how a measurement comes back empty while the thing it meant
            // to measure is happening on screen.
            ViewLine(report, "CIViewBaseLoadout", CIViewBaseLoadout.ins);
            ViewLine(report, "CIViewBaseParts", CIViewBaseParts.ins);
            ViewLine(report, "CIViewBaseInventory", CIViewBaseInventory.ins);
            ViewLine(report, "CIViewBasePilots", CIViewBasePilots.ins);
            ViewLine(report, "CIViewBaseCustomizationRoot", CIViewBaseCustomizationRoot.ins);
            ViewLine(report, "CIViewBaseWorkshopV2", CIViewBaseWorkshopV2.ins);
            ViewLine(report, "CIViewOverworldRoster", CIViewOverworldRoster.ins);
            ViewLine(report, "CIViewOverworldRoot", CIViewOverworldRoot.ins);
            ViewLine(report, "CIViewOverworldNav", CIViewOverworldNav.ins);
            ViewLine(report, "CIViewPauseRoot", CIViewPauseRoot.ins);
        }

        private static void ViewLine(StringBuilder report, string name, CIView? view)
        {
            report.Append("  ").Append(name).Append(": ");
            if (view == null)
            {
                report.Append("NULL\n");
                return;
            }

            report.Append("present | entered: ").Append(view.IsEntered()).Append('\n');
        }

        // --- pbj.ow-sample: measurement 1, one line per second ---

        /// <summary>
        /// Samples the clock once a second for <paramref name="seconds"/>. Run it
        /// while idle on the map, while travelling, during a time skip
        /// (ow.sim-lock), and while sitting in the workshop — the four states
        /// the "continuous clock" claim has to survive.
        /// </summary>
        public static string OverworldSample(int seconds)
        {
            if (host == null)
            {
                return Tag + " no coroutine host (Heartbeat.Start postfix never ran?)";
            }

            if (sampling)
            {
                return Tag + " already sampling";
            }

            if (seconds < 1 || seconds > 600)
            {
                return Tag + " seconds must be 1..600";
            }

            sampling = true;
            host.StartCoroutine(SampleLoop(seconds));
            return $"{Tag} sampling for {seconds}s — watch the log";
        }

        private static IEnumerator SampleLoop(int seconds)
        {
            Debug.Log($"{Tag} sample start | {seconds}s");

            for (var i = 0; i < seconds; i++)
            {
                // Unscaled: the whole point is to keep sampling when
                // Time.timeScale is 0, which is the state under test.
                yield return new WaitForSecondsRealtime(1f);

                string line;
                try
                {
                    line = SampleLine(i);
                }
                catch (Exception e)
                {
                    line = $"{Tag} sample {i} FAILED: {e.GetType().Name}: {e.Message}";
                }

                Debug.Log(line);
            }

            sampling = false;
            Debug.Log($"{Tag} sample end");
        }

        private static string SampleLine(int index)
        {
            var overworld = Contexts.sharedInstance.overworld;
            var controller = GameController.GetInstance();
            var b = IDUtility.playerBaseOverworld;

            var scale = overworld.hasSimulationTimeScale ? overworld.simulationTimeScale.f : float.NaN;
            var time = overworld.hasSimulationTime ? overworld.simulationTime.f : float.NaN;

            return $"{Tag} s{index:D3} | state {controller?.TopState?.Name ?? "<null>"}"
                 + $" | scale {scale:F3} | simTime {time:F4}"
                 + $" | moving {overworld.isBaseMoving}"
                 + $" | lock {overworld.hasSimulationLockCountdown}"
                 + $" | timeScale {Time.timeScale:F3}"
                 + $" | autoSaveTimer {SettingUtility.autoSaveTimer:F1}"
                 + $" | pos {(b != null && b.hasPosition ? b.position.v.ToString("F1") : "<none>")}"
                 + $" | rendered {(b != null && b.hasPositionDetectedLast ? b.positionDetectedLast.v.ToString("F1") : "<none>")}";
        }

        // --- pbj.ow-mirror: measurement 3, the host driving a client's base ---

        /// <summary>
        /// Applies the game's OWN teleport recipe to the player base and logs
        /// what moved. This is the candidate mechanism for "the client watches
        /// the host drive", tried from outside the sim with the clock stopped.
        /// </summary>
        /// <remarks>
        /// Cribbed from ConsoleCommandsOverworld:893-901 rather than invented.
        /// Every step earns its place:
        ///   StopMovement            — or the client's own path fights the write
        ///   ReplacePosition         — the authoritative value
        ///   ReplacePositionTarget   — NOT optional. OverworldMovementSystem
        ///                             :254-272 drags Position back toward a
        ///                             stale PositionTarget whenever the clock
        ///                             runs, so a mirror without it snaps back.
        ///   isPositionUnchecked     — the Y-snap helper
        ///                             (OverworldPositionValidationSystem:15-41)
        ///   ReplaceSimulationTime(same value) — a self-replace that kicks every
        ///                             SimulationTime collector while paused.
        ///                             This is what makes OverworldRangeSystem
        ///                             copy position into positionDetectedLast,
        ///                             which is what PositionLinkSystem renders.
        /// </remarks>
        public static string OverworldMirror(float x, float z)
        {
            var b = IDUtility.playerBaseOverworld;
            if (b == null || !b.hasPosition)
            {
                return Tag + " no player base with a position";
            }

            var overworld = Contexts.sharedInstance.overworld;
            var before = b.position.v;
            var renderedBefore = b.hasPositionDetectedLast ? b.positionDetectedLast.v : Vector3.zero;
            var target = new Vector3(x, before.y, z);

            OverworldUtility.StopMovement(b);
            b.ReplacePosition(target);
            b.ReplacePositionTarget(target);
            b.isPositionUnchecked = true;
            if (overworld.hasSimulationTime)
            {
                overworld.ReplaceSimulationTime(overworld.simulationTime.f);
            }

            var renderedAfter = b.hasPositionDetectedLast ? b.positionDetectedLast.v : Vector3.zero;

            var line = $"{Tag} mirror | state {GameController.GetInstance()?.TopState?.Name ?? "<null>"}"
                     + $" | pos {before:F1} -> {b.position.v:F1}"
                     + $" | rendered {renderedBefore:F1} -> {renderedAfter:F1}"
                     + $" | scale {(overworld.hasSimulationTimeScale ? overworld.simulationTimeScale.f : float.NaN):F3}";

            Debug.Log(line);

            // The rendered value updating IN THE SAME FRAME would be the
            // surprise — OverworldRangeSystem is reactive, so expect it on the
            // next tick, and expect it NOT to arrive at all outside game state
            // "overworld". Re-run pbj.ow-probe a moment later to see which.
            return line + " | re-run pbj.ow-probe to see the settled value";
        }

        // --- pbj.ow-watch: measurement 4, control entry observed not grepped ---

        public static string OverworldWatch()
        {
            Watching = !Watching;
            var line = $"{Tag} control watch {(Watching ? "ON" : "OFF")}";
            Debug.Log(line);
            return line;
        }

        internal static void RegisterConsoleCommands()
        {
            var probe = typeof(OverworldProbeGlue).GetMethod(nameof(OverworldProbe), BindingFlags.Static | BindingFlags.Public);
            var sample = typeof(OverworldProbeGlue).GetMethod(nameof(OverworldSample), BindingFlags.Static | BindingFlags.Public);
            var mirror = typeof(OverworldProbeGlue).GetMethod(nameof(OverworldMirror), BindingFlags.Static | BindingFlags.Public);
            var watch = typeof(OverworldProbeGlue).GetMethod(nameof(OverworldWatch), BindingFlags.Static | BindingFlags.Public);

            QuantumConsoleProcessor.TryAddCommand(new CommandData(probe, "pbj.ow-probe"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(sample, "pbj.ow-sample"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(mirror, "pbj.ow-mirror"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(watch, "pbj.ow-watch"));
        }

        internal static void Log(string what, string detail)
        {
            if (Watching)
            {
                Debug.Log($"{Tag} CONTROL {what} | {detail} | state {GameController.GetInstance()?.TopState?.Name ?? "<null>"}");
            }
        }
    }

    // --- the control-entry patches (measurement 4) ---
    //
    // Every one of these is a candidate suppression point for M12a: on a client
    // they are the routes by which a player could drive a world they do not own.
    // Logging them under a live session says which actually fire on a click,
    // rather than which look like they might.

    /// <summary>
    /// The player's movement order — and the funnel for all three UI routes to
    /// it (CIViewOverworldNav:1060, CIViewOverworldProcess:854,
    /// OverworldMoveToOrderSystem:128). It also UNPAUSES the clock from
    /// simulationTimeScaleBackUp (OverworldUtility:492+), so it is both the
    /// movement entry and a clock entry.
    /// </summary>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(OverworldUtility), nameof(OverworldUtility.OrderMovementToPosition))]
    internal static class Patch_OverworldUtility_OrderMovementToPosition
    {
        private static void Prefix(OverworldEntity entityOverworld, Vector3 destinationPosition)
        {
            var isBase = entityOverworld != null
                      && IDUtility.playerBaseOverworld != null
                      && ReferenceEquals(entityOverworld, IDUtility.playerBaseOverworld);
            OverworldProbeGlue.Log("OrderMovementToPosition", $"playerBase {isBase} | to {destinationPosition:F1}");
        }
    }

    /// <summary>
    /// The time-scale buttons and the pause key. CIViewOverworldRoot:174-237
    /// routes them here; ForceTimeScale (:291-307) bypasses RefreshTimeScale
    /// entirely and is reachable from ow.set-timescale.
    /// </summary>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(OverworldTimeUtility), nameof(OverworldTimeUtility.SetPreferredTimeScale))]
    internal static class Patch_OverworldTimeUtility_SetPreferredTimeScale
    {
        private static void Prefix(float timeScale)
        {
            OverworldProbeGlue.Log("SetPreferredTimeScale", $"to {timeScale:F2}");
        }
    }

    /// <summary>
    /// Combat entry. Measurement 6 needs this from the machine that did NOT
    /// start the mission — M11's turn barrier assumes combat has already begun
    /// and nothing in the mod covers the transition into it.
    /// </summary>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(ScenarioSetupUtility), nameof(ScenarioSetupUtility.EnterCombat))]
    internal static class Patch_ScenarioSetupUtility_EnterCombat
    {
        private static void Prefix()
        {
            OverworldProbeGlue.Log("EnterCombat", "combat entry from the overworld");
        }
    }
}
