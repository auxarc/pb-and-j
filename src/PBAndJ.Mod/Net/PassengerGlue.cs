using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Overworld;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M12a: a client is a passenger on the overworld. It sees everything and
    /// drives nothing.
    /// </summary>
    /// <remarks>
    /// The policy itself lives in <see cref="PassengerRules"/> under the coverage
    /// gate; this file is only the wiring, and deliberately holds no decisions.
    /// <para>
    /// Every refusal is announced through the game's own overworld log
    /// (<c>CIViewOverworldLog.AddMessageWarning</c>) rather than swallowed. That
    /// is the milestone's principle applied to the smallest case: a refusal has
    /// to be visible before the fact, because a silent no and a broken button
    /// are indistinguishable to the person clicking. The connect screen paid for
    /// this lesson once already, where a working connection and a silent failure
    /// looked identical.
    /// </para>
    /// <para>
    /// Greying the buttons themselves is the better ending and is deliberately
    /// not here: it means finding and disabling four separate NGUI controls
    /// across three screens, and doing it wrong strands tooltips (the
    /// <c>ForceHoverEvent</c> trap from M10's close-out). Announcing the refusal
    /// is correct on its own and does not have to be undone to add the greying
    /// later.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class PassengerGlue
    {
        /// <summary>
        /// Which seat this machine is in, as the rules want it.
        /// </summary>
        /// <remarks>
        /// Reads the live session rather than <c>MultiplayerCampaign.Active</c>:
        /// the campaign flag says a co-op save is loaded, which stays true after
        /// a disconnect. Authority is a property of the connection, so a peer
        /// whose host has gone should not silently inherit the wheel — but it
        /// should not be frozen out of its own singleplayer game either, which
        /// is exactly what <see cref="SessionRole.Solo"/> means here.
        /// </remarks>
        internal static SessionRole Role
        {
            get
            {
                if (!NetGlue.HasSession)
                {
                    return SessionRole.Solo;
                }

                return NetGlue.IsHost ? SessionRole.Host : SessionRole.Client;
            }
        }

        /// <summary>
        /// Whether <paramref name="control"/> may proceed; announces it if not.
        /// </summary>
        /// <returns><c>true</c> to let the original method run.</returns>
        internal static bool Allow(OverworldControl control)
        {
            if (PassengerRules.Allows(Role, control))
            {
                return true;
            }

            Announce(control);
            return false;
        }

        private static void Announce(OverworldControl control)
        {
            var refusal = PassengerText.Refusal(control);

            // The overworld log is the right channel for this: it is already
            // where the game tells the player what the world just did, it costs
            // no screen construction, and it cannot be missed the way a console
            // line can. A modal would be unbearable here — a refused map click
            // happens as fast as the player can click the map.
            try
            {
                CIViewOverworldLog.AddMessageWarning(refusal);
            }
            catch (System.Exception e)
            {
                // Never let the announcement break the suppression. The refusal
                // is the load-bearing half; the sentence is a courtesy, and the
                // log view may not exist in every state this runs from.
                Debug.LogWarning($"[pb-and-j] could not post a passenger refusal: {e.GetType().Name}");
            }

            Debug.Log($"[pb-and-j] passenger refused {control}: {refusal}");
        }
    }

    // --- the four suppression points ---
    //
    // The first draft of M12a had one of these and believed it was complete.
    // Each of the other three is a route the 2026-08-08 review measured, by which
    // a passenger could drive a world it does not own.

    /// <summary>
    /// Movement orders. The funnel for all three UI routes
    /// (<c>CIViewOverworldNav:1060</c>, <c>CIViewOverworldProcess:854</c>,
    /// <c>OverworldMoveToOrderSystem:128</c>), and a clock entry too, since it
    /// restarts the sim from <c>simulationTimeScaleBackUp</c>.
    /// </summary>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(OverworldUtility), nameof(OverworldUtility.OrderMovementToPosition))]
    internal static class Patch_Passenger_OrderMovementToPosition
    {
        private static bool Prefix(OverworldEntity entityOverworld)
        {
            // Only the player's own base. This method is the player's funnel but
            // it is not exclusively theirs, and suppressing every caller on a
            // client would freeze the world's own traffic — patrols and convoys
            // that the client is supposed to keep simulating locally. The recon
            // observed only player-base orders here; that is a reason to check
            // rather than a reason to assume.
            var playerBase = IDUtility.playerBaseOverworld;
            if (entityOverworld == null || playerBase == null || !ReferenceEquals(entityOverworld, playerBase))
            {
                return true;
            }

            return PassengerGlue.Allow(OverworldControl.MoveBase);
        }
    }

    /// <summary>
    /// Camp. Runs the shared clock through a <c>SimulationLockCountdown</c>
    /// without issuing any movement order — and its <em>exit</em> re-rolls every
    /// generated contract (<c>CIViewOverworldRoster:1829-1830</c>), which is what
    /// makes this suppression load-bearing for M12b rather than cosmetic.
    /// </summary>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CIViewOverworldRoster), "OnCampInitiated")]
    internal static class Patch_Passenger_OnCampInitiated
    {
        private static bool Prefix() => PassengerGlue.Allow(OverworldControl.Camp);
    }

    /// <summary>
    /// Retreat. Starts a "resupply" lock and a <c>SimulationLockReposition</c>,
    /// so it both runs the clock and relocates the base, entirely outside the
    /// movement funnel above.
    /// </summary>
    /// <remarks>
    /// Patched at <c>OverworldUtility.TryRetreatToResupplyBase</c> rather than at
    /// the roster's button handler, because the utility is where the work
    /// actually happens and the handler only asks it to. Returning false from a
    /// prefix on a bool method leaves the result at its default — false — which
    /// is exactly right here: it reads as "the retreat did not happen", and
    /// <c>OnReturnInitiated</c> already branches on that.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(OverworldUtility), nameof(OverworldUtility.TryRetreatToResupplyBase))]
    internal static class Patch_Passenger_TryRetreatToResupplyBase
    {
        private static bool Prefix() => PassengerGlue.Allow(OverworldControl.Retreat);
    }

    /// <summary>
    /// Mission entry. Reachable on a client through its own site-interaction
    /// dialog once the mirror has put its base in range of something.
    /// </summary>
    /// <remarks>
    /// This is suppression only, not the co-op combat transition. What a client
    /// experiences when the <em>host</em> starts a mission is M12b's work, and
    /// the hook for it is not here: <c>EnterCombat</c> is the briefing-open,
    /// guarded on game state <c>overworld</c>, with a cancel path back out. The
    /// commit is <c>OnLoadingInitiate</c>.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(ScenarioSetupUtility), nameof(ScenarioSetupUtility.EnterCombat))]
    internal static class Patch_Passenger_EnterCombat
    {
        private static bool Prefix() => PassengerGlue.Allow(OverworldControl.EngageSite);
    }
}
