using System;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// "Act as if the player did X" — the levers a script, or eventually the mod
    /// itself, pulls instead of a human clicking.
    /// </summary>
    /// <remarks>
    /// <b>Not part of the dev-only drive rig, deliberately.</b> The rig is the
    /// socket, and that can never ship (<c>PBJ_DRIVE</c>, and
    /// <c>make check-no-drive-channel</c>). These are ordinary console commands,
    /// gated by the game's own developer mode like every other <c>pbj.*</c>
    /// command, and they exist because co-op will eventually have the host drive
    /// a client through exactly these seams. If the rig used a private path and
    /// the mod used another, the rig would prove nothing about the mod.
    /// <para>
    /// Every actuator here goes in through the <em>same</em> entry point the UI
    /// uses, never the implementation beneath it. That is the whole discipline of
    /// this file, and there is a scar behind it — see <see cref="Execute"/>.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class ActuatorGlue
    {
        /// <summary>The Harmony id this assembly's patches are owned by.</summary>
        /// <remarks>
        /// Captured at load rather than hardcoded: the game assigns it
        /// (<c>ModManager.TryLoadingLibraries</c> does <c>new Harmony(id)</c>),
        /// so a literal here would be a second copy of a fact we do not own.
        /// </remarks>
        private static string? harmonyId;

        internal static void RememberHarmonyId(string id)
        {
            harmonyId = id;
        }

        /// <summary>
        /// Presses Execute, exactly as the button does.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>Through <c>CheckAndAttemptExecution</c>, never
        /// <c>CombatUtilities.ConfirmExecution</c>.</b> The turn barrier's gate is
        /// a prefix on this UI method (<c>ExecutionPatches.cs:24-35</c>): inside a
        /// session it swallows the local execution and posts a Ready instead.
        /// <c>ConfirmExecution</c> is one layer below and carries only a
        /// <em>detector</em> (<c>:75-102</c>), whose own comment observes that
        /// "the debug console can [bypass the barrier] too". Driving that instead
        /// would advance the turn locally inside a networked session — silent,
        /// one-sided divergence, the sharpest failure this mod has. The deleted
        /// <c>pbj.commit</c> did precisely that, which is why it is not what came
        /// back.
        /// <para>
        /// So this is not merely a working lever, it is the honest one: with a
        /// session live it readies, and without one it executes. Same as the
        /// button, because it <em>is</em> the button's method.
        /// </para>
        /// </remarks>
        public static string Execute()
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat — nothing to execute";
            }

            var view = CIViewCombatExecution.ins;
            if (view == null || !view.IsEntered())
            {
                return "[pb-and-j] the execution view is not open";
            }

            view.CheckAndAttemptExecution();
            return "[pb-and-j] execute pressed — "
                + (NetGlue.HasSession ? "readied through the barrier" : "run locally (no session)");
        }

        /// <summary>
        /// Selects one of this machine's units, as pressing F1–F6 does.
        /// </summary>
        /// <remarks>
        /// This exists because of a refusal that went unexplained in the notes
        /// for weeks: <b>the combat HUD is not entered until a unit is
        /// selected</b>, and arriving in combat by <i>loading a save</i> selects
        /// nothing. So a freshly-loaded machine sits in combat with no HUD, and
        /// <see cref="Execute"/> refuses with "the execution view is not open"
        /// forever. It looked like a client-side bug; it is simply how the game
        /// starts a loaded fight, and a client follows a host in by loading a
        /// save.
        /// <para>
        /// <c>CIViewCombatMode.OnUnitSelectionByIndex</c> is the very method the
        /// F1–F6 bindings call (<c>InputCombatShared.cs:153-157</c>) — the
        /// player's own path, per this file's discipline, not
        /// <c>ReplaceUnitSelected</c> underneath it.
        /// </para>
        /// <para>
        /// It reports what happened rather than that it pressed. Both
        /// <c>OnUnitSelectionByIndex</c> and the <c>OnUnitClick</c> beneath it
        /// return void and refuse silently — on an out-of-range index, on a
        /// locked feature, on an open tutorial — which is exactly the shape that
        /// made <c>pbj.briefing-deploy</c> report success for a briefing that had
        /// refused.
        /// </para>
        /// </remarks>
        public static string SelectUnit(int index = 0)
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat — nothing to select";
            }

            var view = CIViewCombatMode.ins;
            if (view == null)
            {
                return "[pb-and-j] the combat unit bar does not exist";
            }

            var before = SelectedUnitId();
            view.OnUnitSelectionByIndex(index);
            var after = SelectedUnitId();

            if (after == -1)
            {
                return "[pb-and-j] unit selection refused — index " + index
                    + " of " + DisplayedUnitCount() + " on the bar";
            }

            var unit = IDUtility.GetCombatEntity(after);
            var persistent = unit != null ? IDUtility.GetLinkedPersistentEntity(unit) : null;
            var name = persistent != null && persistent.hasNameInternal
                ? persistent.nameInternal.s
                : "?";
            return "[pb-and-j] selected unit " + index + ": " + name
                + (after == before ? " (already selected)" : string.Empty);
        }

        private static int SelectedUnitId()
        {
            var combat = Contexts.sharedInstance.combat;
            return combat.hasUnitSelected ? combat.unitSelected.id : -1;
        }

        // The list OnUnitSelectionByIndex actually indexes is private, and the
        // public GetUnitSortedList is a DIFFERENT list — reporting that one
        // would make an out-of-range index look in-range. Reflection, or say
        // nothing at all; never a number that might be a lie.
        private static string DisplayedUnitCount()
        {
            var field = typeof(CIViewCombatMode).GetField(
                "unitCombatIDsDisplayed", BindingFlags.Instance | BindingFlags.NonPublic);
            var value = field?.GetValue(CIViewCombatMode.ins) as System.Collections.ICollection;
            return value != null ? value.Count.ToString() : "an unknown number";
        }

        /// <summary>
        /// Presses Deploy on the mission briefing, which raises the confirmation
        /// dialog rather than committing.
        /// </summary>
        /// <remarks>
        /// The briefing is not skippable in practice: of the 56 shipped
        /// scenarios only six set <c>loadImmediately</c>, and all six are
        /// debug or intro content. So anything driving a campaign mission goes
        /// through here and then <see cref="DialogConfirm"/>.
        /// <para>
        /// <c>OnDeployStart</c> rather than <c>ScenarioSetupUtility.OnLoadingInitiate</c>:
        /// the real funnel is the private <c>ConfirmCombat</c>
        /// (<c>CIViewBaseBriefingV2.cs:1935</c>), which validates the site and
        /// writes <c>autosave_before_combat</c> before it initiates loading.
        /// Calling <c>OnLoadingInitiate</c> straight would skip the validation and
        /// the autosave — a shortcut that works right up until the save it did not
        /// write is the one somebody needed.
        /// </para>
        /// </remarks>
        public static string BriefingDeploy()
        {
            // A null check is NOT enough, and this cost a round trip to learn.
            // The game's views persist for the process and sit dormant rather
            // than being created on demand, so `ins` is non-null on the main menu
            // and OnDeployStart threw straight through the guard. Entered is the
            // question worth asking; existing is not.
            var view = CIViewBaseBriefingV2.ins;
            if (view == null || !view.IsEntered())
            {
                return "[pb-and-j] the briefing is not open";
            }

            // ⚠️ Ask BEFORE pressing, because pressing tells you nothing.
            // OnDeployStart opens with `if (!IsDeploymentPossible()) {
            // OnWarningFlash(); return; }` — the refusal is a flash of colour on
            // a screen nobody is watching, and the method returns void either
            // way. Reporting "deploy pressed" on that path is the connect-screen
            // bug again: silent success indistinguishable from silent failure.
            // A squad with no pilot assigned lands here, and it looked exactly
            // like a confirmation dialog that would not open. (2026-08-14)
            if (!view.IsDeploymentPossible())
            {
                return "[pb-and-j] deployment is not possible — the briefing refused "
                    + "(squad not ready: check pilots, frame integrity and damage limits)";
            }

            view.OnDeployStart();
            return "[pb-and-j] deploy pressed — confirm with pbj.dialog-confirm";
        }

        /// <summary>
        /// Presses the navigation bar's World button — the base-to-map transition.
        /// </summary>
        /// <remarks>
        /// Needed because a synchronised load lands both machines in
        /// <c>basecrawler</c>, the base interior, while every overworld console
        /// command guards on game state <c>overworld</c> and refuses silently:
        /// <c>OverworldStateCheck</c> writes "Command only available from the
        /// overworld" to the Quantum Console's own view, which never reaches
        /// <c>Player.log</c> and comes back over the drive channel as an empty
        /// reply. A scripted fight therefore looked like a scenario that would not
        /// load, when the real answer was that nobody had left the base.
        /// <para>
        /// Drives <c>CIViewOverworldNav.OnClickWorld</c> — the player's own button
        /// handler — rather than writing <c>GameControllerStatePopRequest</c>
        /// directly. The handler is private, so this is reflection; the state
        /// component is public, so writing it would have been easier and wrong.
        /// It carries a transition check and a combat-exit branch, and an actuator
        /// that skips those proves nothing about the path a player takes.
        /// </para>
        /// </remarks>
        public static string NavWorld()
        {
            var nav = CIViewOverworldNav.ins;
            if (nav == null || !nav.IsEntered())
            {
                return "[pb-and-j] the overworld navigation bar is not up";
            }

            if (IDUtility.IsGameState("overworld"))
            {
                return "[pb-and-j] already on the overworld";
            }

            var click = AccessTools.Method(typeof(CIViewOverworldNav), "OnClickWorld");
            if (click == null)
            {
                return "[pb-and-j] CIViewOverworldNav.OnClickWorld is gone — the game build moved";
            }

            click.Invoke(nav, null);
            return "[pb-and-j] world pressed — poll pbj.drive-state for state=overworld";
        }

        /// <summary>
        /// Presses the confirm button on whatever confirmation dialog is open.
        /// </summary>
        /// <remarks>
        /// Invokes the button's own <c>UICallback</c> rather than reaching for the
        /// dialog's private <c>OnConfirm</c>, so the dialog still runs its exit
        /// and audio the way a click would. Safe to invoke only because
        /// <c>CIViewDialogConfirmation.Awake</c> <em>constructs</em> the callback
        /// (<c>:48</c>): <c>UICallback.type</c> is <c>[NonSerialized]</c>, so a
        /// callback that came from a prefab would report type <c>Void</c>
        /// regardless of what it actually holds, and invoking one of those is a
        /// silent no-op. This one is built at runtime, so its type is real.
        /// </remarks>
        public static string DialogConfirm()
        {
            // ⚠️ The entered check is load-bearing, not tidiness. The dialog is a
            // dormant singleton whose confirm button is wired once in Awake, and
            // `callbackOnConfirm` holds whatever the LAST caller set. Invoking it
            // while nothing is open therefore does not fail — it silently re-runs
            // a previous confirmation. Observed: this returned "dialog confirmed"
            // from the main menu.
            var dialog = CIViewDialogConfirmation.ins;
            if (dialog == null || !dialog.IsEntered())
            {
                return "[pb-and-j] no confirmation dialog is open";
            }

            var button = dialog.buttonConfirm;
            if (button == null || button.callbackOnClick == null)
            {
                return "[pb-and-j] the dialog has no confirm button wired";
            }

            button.callbackOnClick.Invoke();
            return "[pb-and-j] dialog confirmed";
        }

        /// <summary>
        /// Agrees to the lobby's chosen save, as the lobby screen's Ready does.
        /// </summary>
        /// <remarks>
        /// ⚠️ <c>pbj.ready</c> is NOT this. That posts <c>LocalReadyEvent</c> — the
        /// combat turn barrier — and the two are unrelated barriers with
        /// confusingly similar names. Before these commands existed, lobby
        /// readiness was reachable only by clicking the lobby screen, so a client
        /// could not be driven into a synchronised load at all. That gap is the
        /// sort this rig exists to surface: the host will eventually need to do
        /// this to a client, and it had no seam.
        /// </remarks>
        public static string LobbyReady()
        {
            if (!NetGlue.HasSession)
            {
                return "[pb-and-j] no session";
            }

            NetGlue.PostLocalLobbyReady();
            return "[pb-and-j] lobby ready posted";
        }

        /// <summary>Withdraws lobby agreement.</summary>
        public static string LobbyUnready()
        {
            if (!NetGlue.HasSession)
            {
                return "[pb-and-j] no session";
            }

            NetGlue.PostLocalLobbyUnready();
            return "[pb-and-j] lobby unready posted";
        }

        /// <summary>
        /// One machine-readable line of everything a driving script needs.
        /// </summary>
        /// <remarks>
        /// Exists so a script polls a fact rather than sleeping a guess. That is
        /// not fussiness: writing the fight at combat entry waits on
        /// <c>CanSave</c>, which was measured taking 6.5 seconds, so anything
        /// timed rather than observed races it.
        /// <para>
        /// The patch count is in here because a half-applied patch set is this
        /// mod's worst failure mode and is otherwise invisible —
        /// <c>ModLink.OnLoad</c> wraps <c>PatchAll</c> in try/finally with no
        /// catch, so one throwing patch silently drops every patch after it. A
        /// number that disagrees with the expected one means the suppression gates
        /// are only partly live, and nothing else on screen would say so.
        /// </para>
        /// </remarks>
        public static string DriveState()
        {
            var combat = Contexts.sharedInstance.combat;
            var turn = combat.hasCurrentTurn ? combat.currentTurn.i : -1;

            return string.Join(" | ", new[]
            {
                "state=" + IDUtility.GetGameState(),
                "combat=" + IDUtility.IsGameState("combat"),
                "turn=" + turn,
                "simulating=" + combat.Simulating,
                // The combat clock, and the planning clock that runs ahead of
                // it. A client never advances simulationTime — ~38 reactive
                // systems trigger on it and most do not self-gate on Simulating
                // — so the gap between these two grows on a client and not on a
                // host. The game's own overlay reads exactly that difference
                // against a unit's predictionTimeHorizon to decide whether to
                // show "no data", which is why it is worth being able to see.
                "simTime=" + (combat.hasSimulationTime
                    ? combat.simulationTime.f.ToString("0.00", CultureInfo.InvariantCulture)
                    : "-"),
                "predTime=" + (combat.hasPredictionTime
                    ? combat.predictionTime.f.ToString("0.00", CultureInfo.InvariantCulture)
                    : "-"),
                "session=" + NetStatusShort(),
                // M14. Cumulative within a window, deliberately: most effects
                // last under a second, so a live count polled between two of
                // them reads zero and a turn full of gunfire is
                // indistinguishable from one where nothing fired. The third
                // number is the one that means something is wrong rather than
                // quiet — an effect the client could not show at all.
                "effects=" + KeyframePlayer.ShownEffects
                    + "/" + KeyframePlayer.RevealedEffects
                    + "/" + KeyframePlayer.UnplayableEffects,
                // The interval-activation measurement, as two ratios: how many
                // effects were revealed after their own window had closed and
                // drew a particle, against the same for effects revealed on
                // time. The second is the control — without it a low late rate
                // could just mean these effects are sparse.
                "late=" + KeyframePlayer.LateDrawing + "/" + KeyframePlayer.LateReveals
                    + " ontime=" + KeyframePlayer.OnTimeDrawing + "/" + KeyframePlayer.OnTimeReveals,
                // M14 measurement 2. beams= is the precondition check — a run
                // whose turn carried no beams answers nothing about beams and
                // must not be mistaken for a clean result. tsim= is the shader
                // global's value at the window's two ends, which with the mirror
                // OFF is the client's real precondition. overwrites= is the one
                // that decides whether the run counts at all: any frame on which
                // something else wrote the global means another writer is
                // competing with the mirror, and a confounded A/B looks exactly
                // like the answer we hope for.
                "beams=" + KeyframePlayer.BeamsRevealed + "/" + KeyframePlayer.BeamsBuilt
                    + " tsim=" + KeyframePlayer.TimeSimAtStart.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + "->" + KeyframePlayer.TimeSimAtEnd.ToString(
                        "0.00", CultureInfo.InvariantCulture)
                    + " overwrites=" + KeyframePlayer.TimeSimOverwrites
                    + " mirror=" + (KeyframePlayer.MirrorTimeSimulation ? "on" : "off"),
                // M14 stage B. Both are expected to read zero, and both are
                // losses nothing on screen would show: a bullet without its wake
                // still flies the right path, and a muzzle flash that never lit
                // is invisible among the ones that did. trailsRefused> 0 means
                // this client's projectile prefab has no AraTrail where the
                // host's did — which the pool digest cannot see, since it hashes
                // pool keys and not the components hanging off each prefab.
                "lightsFired=" + KeyframePlayer.LightsFired
                    + " lightsNoMgr=" + KeyframePlayer.LightsNoManager
                    + " trailsRefused=" + KeyframePlayer.TrailsRefused
                    + " lightsRefused=" + KeyframePlayer.LightsRefused
                    + " lightsNoTransform=" + WeaponLightPatches.SkippedNoTransform,
                // The launch splash — logos, then the seizure warning. It sits
                // OVER the main menu while the game already reports
                // state=mainmenu, so a script that treats that state as "ready"
                // drives commands into a game whose intro has not finished. A
                // load sent into that gap succeeds and then the pending intro
                // drops CIViewPauseRoot on top of the loaded battle, which no
                // other field here can see.
                "splash=" + (CIViewSplashScreen.ins != null
                    && CIViewSplashScreen.ins.IsEntered()),
                // ⚠️ WAIT ON THIS ONE, NOT ON splash. `splash` is false twice —
                // before the view enters as well as after it leaves — so a poll
                // that starts early passes instantly and drives a game that has
                // not finished starting. The title menu going up is monotonic
                // in the direction that matters, and it is what the splash is
                // holding back.
                "menu=" + (CIViewPauseRoot.ins != null
                    && CIViewPauseRoot.ins.IsEntered() && CIViewPauseRoot.ins.mainMode),
                "patched=" + PatchedMethodCount(),
                "canSave=" + SafeCanSave(),
                // M8. A script must never commit a turn while a window is
                // still playing — the units are asleep and their animators are
                // off until the unwind runs, and the only lever that would let
                // it happen anyway is the barrier bypass this rig already knows
                // not to use.
                // "held" rather than a turn label when playback is frozen at a
                // hold point: a held window never reaches its end, so it would
                // otherwise be indistinguishable from one still playing — and
                // every await_idle in the playtest scripts would sit on it until
                // it timed out with no clue why.
                "replay=" + (KeyframePlayer.Holding
                    ? "held@" + KeyframePlayer.HoldAt.ToString("0.00", CultureInfo.InvariantCulture)
                    : KeyframePlayer.IsPlaying
                        ? "turn" + KeyframePlayer.Turn + "/" + KeyframePlayer.PosedUnits + "posed"
                        : "idle"),
            });
        }

        private static string NetStatusShort()
        {
            if (!NetGlue.HasSession)
            {
                return "none";
            }

            return (NetGlue.IsHost ? "host" : "client") + "," + NetGlue.NetStatus();
        }

        private static string SafeCanSave()
        {
            try
            {
                return PhantomBrigade.Data.DataManagerSave.CanSave(false).ToString();
            }
            catch (Exception e)
            {
                return "?" + e.GetType().Name;
            }
        }

        /// <summary>
        /// How many distinct methods this assembly's Harmony id has patched.
        /// </summary>
        /// <remarks>
        /// Methods, not patch classes. Several targets are patched twice, so the
        /// two numbers differ and an assertion written against the class count
        /// would fail on a healthy build.
        /// </remarks>
        private static string PatchedMethodCount()
        {
            if (harmonyId == null)
            {
                return "?";
            }

            try
            {
                return Harmony.GetAllPatchedMethods()
                    .Count(m => Harmony.GetPatchInfo(m)?.Owners?.Contains(harmonyId) == true)
                    .ToString();
            }
            catch (Exception e)
            {
                return "?" + e.GetType().Name;
            }
        }

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(Execute), "pbj.execute");
            Add(nameof(SelectUnit), new[] { typeof(int) }, "pbj.select-unit");
            Add(nameof(BriefingDeploy), "pbj.briefing-deploy");
            Add(nameof(DialogConfirm), "pbj.dialog-confirm");
            Add(nameof(NavWorld), "pbj.nav-world");
            Add(nameof(LobbyReady), "pbj.lobby-ready");
            Add(nameof(LobbyUnready), "pbj.lobby-unready");
            Add(nameof(DriveState), "pbj.drive-state");
        }

        private static void Add(string methodName, string command)
        {
            Add(methodName, new Type[0], command);
        }

        private static void Add(string methodName, Type[] parameters, string command)
        {
            var method = typeof(ActuatorGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
