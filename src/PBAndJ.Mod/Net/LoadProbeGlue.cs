using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // THROWAWAY, like ReplayProbeGlue and the four probes deleted with M10.
    // This one answers the single question M11 ends on: can
    // DataHelperLoading.TryLoading be driven from outside a load screen's own
    // flow, and does its completion callback actually arrive? M11d's whole
    // "load in unison" design rests on the answer. Delete it once the answers
    // are in docs/design/campaign-coop.md.
    //
    // Reading the decompile has already moved the question twice, which is
    // exactly why this exists rather than another careful read:
    //
    //   * TryLoading is called from EIGHT classes, not just CIViewPauseLoad.
    //     Two are not view code at all — the game's own `load` console command
    //     (ConsoleCommandsShared:74) and DataManagerSave.QuickLoad:3518, which
    //     loads by key WITH a callback from arbitrary game states. So the
    //     shipping game already does what M11d wants. load-try below is
    //     deliberately parameterised like QuickLoad, since that is the call the
    //     game itself proves works.
    //
    //   * The real hazard is not in TryLoading, it is in LoadingEnd2 — the only
    //     place the callback fires (:384). Before reaching it, LoadingEnd2
    //     dereferences CIViewPauseRoot.ins, CIViewPauseLoad.ins.sidebarHelper
    //     .buttonConfirm and CIViewOverworldLog.ins UNCONDITIONALLY (:376-378).
    //     A null on any of them throws before the callback runs — including a
    //     hard dependency on the load SCREEN's singleton for a load that never
    //     touched it. That is what section "completion-path singletons"
    //     measures.
    //
    // Console return values do NOT reach Player.log — Quantum Console renders
    // them in its own view — so everything worth keeping is Debug.Log'd.
    [ExcludeFromCodeCoverage]
    internal static class LoadProbeGlue
    {
        private const string Tag = "[pb-and-j] load-probe";

        /// <summary>
        /// Read-only. Safe to run from any game state, and the thing to run
        /// BEFORE and AFTER a load-try.
        /// </summary>
        public static string LoadProbe()
        {
            var report = new StringBuilder();
            report.Append(Tag).Append('\n');

            Section(report, "game state", ProbeGameState);
            Section(report, "completion-path singletons", ProbeSingletons);
            Section(report, "save headers", ProbeSaveHeaders);

            Debug.Log(report.ToString());
            return Tag + " written to the log";
        }

        /// <remarks>
        /// One section failing must not cost the others.
        /// </remarks>
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

        // --- Q1: the two flags that gate TryLoading, and the state it demands ---

        /// <remarks>
        /// TryLoading:226 returns early — LogWarning only, callback never fires
        /// — when isTeardownOfCampaignRequested || isLoadingInProgress. And
        /// LoadingStart:259 bails when the controller state is not "mainmenu",
        /// WITHOUT clearing isLoadingInProgress that TryLoading:234 just set.
        /// The clears live at :270, :277, :306 and :375, none of them that
        /// branch — so losing the state race plausibly wedges loading for the
        /// rest of the process. isTeardownOfCampaignRequested does have a
        /// rescuer (TeardownCampaignSystem:57); isLoadingInProgress has none
        /// that a read of this file can find.
        /// <para>
        /// Which is why this command's whole point is to be run AGAIN after a
        /// deliberately failed load. If isLoadingInProgress is still true, M11d
        /// needs a pre-flight check and cannot simply retry.
        /// </para>
        /// </remarks>
        private static void ProbeGameState(StringBuilder report)
        {
            var game = Contexts.sharedInstance.game;

            report.Append("  IDUtility.gameState=").Append(Describe(IDUtility.gameState)).Append('\n');
            report.Append("  hasGameControllerStateCurrent=").Append(game.hasGameControllerStateCurrent)
                .Append(" gameControllerStateCurrent=")
                .Append(game.hasGameControllerStateCurrent
                    ? Describe(game.gameControllerStateCurrent.s)
                    : "-")
                .Append('\n');

            // The two gates, and the pair that decides whether a wedge happened.
            report.Append("  isLoadingInProgress=").Append(game.isLoadingInProgress)
                .Append(" isTeardownOfCampaignRequested=").Append(game.isTeardownOfCampaignRequested)
                .Append("   <-- both must be False for TryLoading to proceed\n");

            // Written only at TryLoading:231 and cleared only at LoadingEnd2:387,
            // so a non-null here after a failed load means a callback is stranded.
            var pending = PrivateStatic("callbackAfterLoading");
            report.Append("  callbackAfterLoading=").Append(pending == null ? "null" : "PENDING")
                .Append(" screenAfterLoading=").Append(PrivateStatic("screenAfterLoading"))
                .Append('\n');
        }

        private static object? PrivateStatic(string field)
        {
            var info = typeof(DataHelperLoading).GetField(
                field, BindingFlags.Static | BindingFlags.NonPublic);
            return info?.GetValue(null);
        }

        // --- Q2: does the completion path have everything it dereferences? ---

        /// <remarks>
        /// LoadingEnd2:376-378 touches the first three unconditionally, before
        /// the callback at :384. CIViewPauseLoad is the interesting one: the
        /// chain is ins.sidebarHelper.buttonConfirm.available, so a load driven
        /// from anywhere at all still needs the load screen's singleton AND its
        /// serialized sidebar reference. Each link is reported separately —
        /// "the screen exists" and "its sidebar is wired" are different answers.
        /// <para>
        /// CIViewBackgroundLoading is reported too but is NOT a completion-path
        /// hazard: at :380 it is guarded by !screenAfterLoading, and it is
        /// dereferenced far earlier in every load (LoadingStart:282-286,
        /// LoadingOverworld:298) and inside the failure handler itself
        /// (OnLoadFailed:252). A null there kills the load long before here.
        /// </remarks>
        private static void ProbeSingletons(StringBuilder report)
        {
            report.Append("  CIViewPauseRoot.ins=").Append(Null(CIViewPauseRoot.ins))
                .Append(" CIViewOverworldLog.ins=").Append(Null(CIViewOverworldLog.ins))
                .Append('\n');

            var load = CIViewPauseLoad.ins;
            report.Append("  CIViewPauseLoad.ins=").Append(Null(load));
            if (load != null)
            {
                var sidebar = load.sidebarHelper;
                report.Append(" .sidebarHelper=").Append(Null(sidebar));
                if (sidebar != null)
                {
                    report.Append(" .buttonConfirm=").Append(Null(sidebar.buttonConfirm));
                }
            }
            report.Append("   <-- the full chain LoadingEnd2:377 walks\n");

            report.Append("  CIViewBackgroundLoading.ins=").Append(Null(CIViewBackgroundLoading.ins))
                .Append(" (needed much earlier, and by OnLoadFailed)\n");
        }

        private static string Null(UnityEngine.Object? o) => o == null ? "NULL" : "ok";

        private static string Null(object? o) => o == null ? "NULL" : "ok";

        // --- Q3: what does the catalogue actually hold, and does the prefix show? ---

        /// <remarks>
        /// GetSaveHeaders is the catalogue M11b builds on, and the pbj_ prefix
        /// decision rests on it listing our saves alongside the player's. Also
        /// confirms the key we would hand TryLoading is a real key.
        /// </remarks>
        private static void ProbeSaveHeaders(StringBuilder report)
        {
            var headers = DataManagerSave.GetSaveHeaders(refresh: true);
            if (headers == null)
            {
                report.Append("  GetSaveHeaders returned null\n");
                return;
            }

            report.Append("  ").Append(headers.Count).Append(" save(s)\n");
            foreach (var pair in headers)
            {
                var meta = pair.Value;
                report.Append("    ")
                    .Append(pair.Key.StartsWith("pbj_", StringComparison.Ordinal) ? "[pbj] " : "      ")
                    .Append(pair.Key)
                    .Append(" | format=").Append(meta == null ? -1 : meta.saveFormat)
                    .Append(" campaign=").Append(meta == null ? -1 : meta.campaignID)
                    .Append(" build=").Append(meta == null ? "?" : Describe(meta.buildInfo))
                    .Append('\n');
            }
        }

        // --- Q4: the real thing ---

        /// <summary>
        /// Actually drives a load. THIS LOADS A SAVE — use a throwaway campaign.
        /// </summary>
        /// <remarks>
        /// Parameterised like DataManagerSave.QuickLoad:3518 — by key, with a
        /// callback, keepScreenAfterLoading: true — because that is the call the
        /// shipping game proves works from arbitrary states.
        /// <para>
        /// The three log lines are the whole experiment, and they are three
        /// rather than one on purpose:
        /// </para>
        /// <list type="bullet">
        /// <item><b>before</b> — the gate flags, so a refusal is attributable.</item>
        /// <item><b>returned</b> — logged synchronously after the call. TryLoading
        /// is void and its early return is a LogWarning, so without this a
        /// silent refusal and a load that started are indistinguishable.</item>
        /// <item><b>CALLBACK</b> — from inside the callback. Its absence is the
        /// finding: the callback is success-only, and M11d cannot use it alone
        /// to detect a peer whose load failed.</item>
        /// </list>
        /// </remarks>
        public static string LoadTry(string saveKey)
        {
            if (string.IsNullOrWhiteSpace(saveKey))
            {
                return Tag + " needs a save key — run pbj.load-probe to list them";
            }

            var game = Contexts.sharedInstance.game;
            Debug.Log(Tag + " load-try '" + saveKey + "' | before"
                + " | state=" + Describe(IDUtility.gameState)
                + " | isLoadingInProgress=" + game.isLoadingInProgress
                + " | isTeardownOfCampaignRequested=" + game.isTeardownOfCampaignRequested);

            try
            {
                DataHelperLoading.TryLoading(
                    saveKey,
                    SaveLocation.Normal,
                    delegate
                    {
                        // The one line that answers the question.
                        Debug.Log(Tag + " load-try '" + saveKey + "' | CALLBACK FIRED"
                            + " | state=" + Describe(IDUtility.gameState));
                    },
                    keepScreenAfterLoading: true);
            }
            catch (Exception e)
            {
                Debug.Log(Tag + " load-try '" + saveKey + "' | THREW "
                    + e.GetType().Name + ": " + e.Message);
                return Tag + " threw — see the log";
            }

            // Synchronous: TryLoading either scheduled the load or refused it,
            // and the flags right now say which.
            Debug.Log(Tag + " load-try '" + saveKey + "' | returned"
                + " | isLoadingInProgress=" + game.isLoadingInProgress
                + " | isTeardownOfCampaignRequested=" + game.isTeardownOfCampaignRequested
                + " | callbackAfterLoading=" + (PrivateStatic("callbackAfterLoading") == null ? "null" : "PENDING"));

            return Tag + " load-try issued — watch the log, then run pbj.load-probe again";
        }

        private static string Describe(string? value) =>
            string.IsNullOrEmpty(value) ? "(none)" : value!;

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(LoadProbe), new Type[0], "pbj.load-probe");
            Add(nameof(LoadTry), new[] { typeof(string) }, "pbj.load-try");
        }

        private static void Add(string methodName, Type[] parameters, string command)
        {
            var method = typeof(LoadProbeGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
