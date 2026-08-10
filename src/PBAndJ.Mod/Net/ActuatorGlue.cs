using System;
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

            view.OnDeployStart();
            return "[pb-and-j] deploy pressed — confirm with pbj.dialog-confirm";
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
                "session=" + NetStatusShort(),
                "patched=" + PatchedMethodCount(),
                "canSave=" + SafeCanSave(),
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
            Add(nameof(BriefingDeploy), "pbj.briefing-deploy");
            Add(nameof(DialogConfirm), "pbj.dialog-confirm");
            Add(nameof(LobbyReady), "pbj.lobby-ready");
            Add(nameof(LobbyUnready), "pbj.lobby-unready");
            Add(nameof(DriveState), "pbj.drive-state");
        }

        private static void Add(string methodName, string command)
        {
            var method = typeof(ActuatorGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, new Type[0], null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
