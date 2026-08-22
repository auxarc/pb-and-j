using System;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M12b: writes the fight the host just entered, at the first moment the game
    /// will allow it.
    /// </summary>
    /// <remarks>
    /// The session cannot do this itself, and the reason is measured rather than
    /// stylistic: <c>DataManagerSave.CanSave(false)</c> refuses while
    /// <c>combat.isScenarioIntroInProgress</c>, and that flag is raised in the
    /// same tick that makes <c>InCombat</c> true. Something has to poll across
    /// frames. A guard for it in Core would be a branch nothing could reach,
    /// which the coverage gate turns into a build failure.
    /// <para>
    /// So Core decides <em>when</em> — <c>HandleCombatEntered</c> emits a
    /// <see cref="ShipCombatEffect"/>, which lands here as <see cref="Arm"/>
    /// inside the same pump that observed the combat edge — and this decides
    /// <em>how</em>, answering with a <c>LocalCombatReadyEvent</c> either way.
    /// </para>
    /// <para>
    /// The per-second line naming what is blocking the save is not debug noise
    /// left in by accident. It is the measurement the design doc asked a throwaway
    /// probe for, and keeping it means the next person to wonder how long a
    /// scenario intro runs reads a log instead of writing a probe.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class CombatShipGlue
    {
        /// <summary>
        /// How long to keep trying before giving up and letting the host fight
        /// alone.
        /// </summary>
        /// <remarks>
        /// Counted against <b>machine-paced</b> refusals only — see
        /// <see cref="Blocker"/>. The entry barrier's own timeout is 120s and
        /// starts only once this has finished, so this is not competing with it.
        /// <para>
        /// <b>Was 30s, and that was too tight to survive a playtest.</b> Real
        /// entries have been measured at 26.7s, 26.9s and 27.4s, every one of
        /// them blocked throughout on "the turn is being simulated" — under ten
        /// percent of margin — and the fourth run went past 30s and lost. The
        /// cost of being too short is not a slow start: the host gives up, drops
        /// the peer with "the fight could not be shared", and fights alone, so a
        /// co-op session ends because a machine was briefly busy. The cost of
        /// being too long is that a genuinely stuck write is noticed later, and
        /// nothing else is waiting on it.
        /// </para>
        /// </remarks>
        private const float ShipTimeoutSeconds = 90f;

        private const float SayWhyEverySeconds = 1f;

        private static bool armed;

        /// <summary>
        /// Whether this write was asked for by hand rather than by the session.
        /// </summary>
        /// <remarks>
        /// <c>pbj.ship-fight</c> exists so the entry-moment save can be produced
        /// and inspected on one machine with nobody else connected. A hand-driven
        /// write reports to the log and <b>never</b> to a session: posting
        /// <c>LocalCombatReadyEvent</c> into a <c>ClientSession</c> used to be a
        /// throw, and <c>NetGlue.Pump</c> turns a throw into "networking stopped"
        /// for the rest of the process.
        /// </remarks>
        private static bool byHand;

        private static float blockedSeconds;
        private static float sinceSaidWhy;

        /// <summary>Asked by the session, through <see cref="ShipCombatEffect"/>.</summary>
        internal static void Arm()
        {
            Begin(byHandRequest: false);
        }

        /// <summary>The <c>pbj.ship-fight</c> console command.</summary>
        public static string ShipFight()
        {
            if (!InAFight())
            {
                return "[pb-and-j] not in a fight — nothing to write";
            }

            Begin(byHandRequest: true);
            return "[pb-and-j] writing the fight to '" + LobbySaveNames.ScenarioSlot
                + "' at the first permitted moment — watch the log";
        }

        private static void Begin(bool byHandRequest)
        {
            if (armed)
            {
                // Already waiting for a moment to write. Re-arming would restart
                // the timeout clock, which is the one thing an accidental second
                // ask must not do.
                return;
            }

            armed = true;
            byHand = byHandRequest;
            blockedSeconds = 0f;
            sinceSaidWhy = 0f;
        }

        /// <summary>
        /// Called every frame, immediately after <c>NetGlue.Pump</c>.
        /// </summary>
        /// <remarks>
        /// That ordering is load-bearing and free: the pump executes its effects
        /// synchronously, so an <see cref="Arm"/> caused by this frame's combat
        /// edge has already happened by the time this runs. Nothing here can
        /// observe a fight the session does not know about.
        /// </remarks>
        internal static void Tick()
        {
            if (!armed)
            {
                return;
            }

            try
            {
                Poll();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not write the fight: "
                    + e.GetType().Name + ": " + e.Message);
                GiveUp();
            }
        }

        private static void Poll()
        {
            if (!InAFight())
            {
                // The fight ended, or was abandoned, before it could be written.
                // Disarmed WITHOUT reporting a failure: the session has already
                // seen the combat edge go the other way and cancelled the entry,
                // and a late failure report would restart it — announcing a fight
                // at turn -1 from a host sitting in its lobby.
                Debug.Log("[pb-and-j] left the fight before it could be written — not shipping it");
                armed = false;
                return;
            }

            if (!byHand && !Hosting())
            {
                Debug.Log("[pb-and-j] no longer hosting — not shipping the fight");
                armed = false;
                return;
            }

            if (!DataManagerSave.CanSave(false))
            {
                WaitAndSayWhy();
                return;
            }

            Write();
        }

        private static void WaitAndSayWhy()
        {
            var reason = Blocker(out var playerPaced);

            // A human reading a tutorial is not a stuck save. Counting their
            // reading time against the timeout would fire the ship-failure path —
            // which drops every peer — on the most likely first co-op fight there
            // is, since that is exactly where combat tutorials appear.
            if (!playerPaced)
            {
                blockedSeconds += Time.unscaledDeltaTime;
            }

            sinceSaidWhy += Time.unscaledDeltaTime;
            if (sinceSaidWhy >= SayWhyEverySeconds)
            {
                sinceSaidWhy = 0f;
                Debug.Log("[pb-and-j] waiting to write the fight: " + reason
                    + " | " + blockedSeconds.ToString("0.0") + "s");
            }

            if (blockedSeconds >= ShipTimeoutSeconds)
            {
                Debug.LogWarning("[pb-and-j] gave up writing the fight after "
                    + blockedSeconds.ToString("0.0") + "s — last blocked by " + reason);
                GiveUp();
            }
        }

        private static void Write()
        {
            // previewScreenshot: false. Not a micro-optimisation — the preview
            // coroutine hides and re-shows several views to take its picture, and
            // the last thing a player entering a battle needs is the UI blinking.
            DataManagerSave.DoSave(
                LobbySaveNames.ScenarioSlot, SaveLocation.Normal, null, -1, false);

            // The same function the receiving side's LoadGlue.BeginCombat compares
            // against, deliberately: two digests computed two ways is how a client
            // ends up re-fetching a fight it is already holding, for ever.
            var digest = SaveCatalogueGlue.Digest(LobbySaveNames.ScenarioSlot);
            if (string.IsNullOrEmpty(digest))
            {
                Debug.LogWarning("[pb-and-j] wrote the fight but could not read it back to check it");
                GiveUp();
                return;
            }

            Debug.Log("[pb-and-j] fight written to '" + LobbySaveNames.ScenarioSlot
                + "' after " + blockedSeconds.ToString("0.0") + "s | digest " + digest);

            armed = false;
            if (byHand)
            {
                // Told, not offered. A hand-driven write is a probe.
                Debug.Log("[pb-and-j] written by hand — not offering it to anyone");
                return;
            }

            NetGlue.PostLocalCombatReady(LobbySaveNames.ScenarioSlot, digest);
        }

        /// <summary>
        /// Reports that this fight cannot be shared, so the host is not left
        /// standing in a battle nobody will ever start.
        /// </summary>
        private static void GiveUp()
        {
            armed = false;
            if (byHand)
            {
                return;
            }

            NetGlue.PostLocalCombatReady(null, null);
        }

        private static bool InAFight()
        {
            return IDUtility.IsGameState("combat")
                && Contexts.sharedInstance.combat.hasCurrentTurn;
        }

        private static bool Hosting()
        {
            return NetGlue.HasSession && NetGlue.IsHost;
        }

        /// <summary>
        /// Which of <c>CanSave</c>'s refusals is the live one, and whether a human
        /// is holding it.
        /// </summary>
        /// <remarks>
        /// <c>internal</c> rather than private because <see cref="CheckpointGlue"/>
        /// needs the same answer at a different moment. Shared rather than copied
        /// deliberately: this walks <c>CanSave</c>'s refusals in <c>CanSave</c>'s
        /// own order, and a second copy would drift out of that order silently and
        /// start naming a condition that is merely true rather than the one that
        /// actually returned false.
        /// </remarks>
        /// <remarks>
        /// Checked in <c>CanSave</c>'s own order (<c>DataManagerSave.cs:95-149</c>)
        /// so the answer names the condition that actually returned false rather
        /// than the first one that happens to be true.
        /// <para>
        /// <b>Player-paced</b> means a person is looking at something and the game
        /// is waiting for them. Those must not run the timeout down; everything
        /// else resolves on its own in a bounded time or never.
        /// </para>
        /// </remarks>
        internal static string Blocker(out bool playerPaced)
        {
            var game = Contexts.sharedInstance.game;
            var combat = Contexts.sharedInstance.combat;
            var persistent = Contexts.sharedInstance.persistent;
            var overworld = Contexts.sharedInstance.overworld;

            playerPaced = false;

            if (game.isLoadingInProgress)
            {
                return "a load is in progress";
            }
            if (!persistent.hasDataKeySave)
            {
                return "this session has no save key";
            }
            if (IDUtility.playerBaseOverworld == null)
            {
                return "there is no player base";
            }
            if (combat.Simulating)
            {
                return "the turn is being simulated";
            }
            if (persistent.hasCombatResolved)
            {
                return "the fight is already resolved";
            }
            if (combat.isScenarioIntroInProgress)
            {
                return "scenario intro";
            }
            if (overworld.hasSimulationLockCountdown)
            {
                return "the overworld clock is locked";
            }
            if (IDUtility.playerBaseOverworld.isCloaked)
            {
                return "the base is cloaked";
            }

            if (game.isCutsceneInProgress)
            {
                playerPaced = true;
                return "a cutscene is playing";
            }
            if (CIViewTutorial.ins.IsEntered())
            {
                playerPaced = true;
                return "a tutorial is open";
            }
            if (CIViewOverworldDebriefing.ins.IsEntered())
            {
                playerPaced = true;
                return "the debriefing is open";
            }

            // CanSave said no and every condition we can name says yes. Machine
            // paced by default: an unnamed blocker we wait on for ever is the
            // failure this whole class exists to avoid.
            return "something unnamed — CanSave says no";
        }
    }
}
