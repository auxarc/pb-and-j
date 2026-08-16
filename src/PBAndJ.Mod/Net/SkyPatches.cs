using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PhantomBrigade;
using PhantomBrigade.Overworld.Systems;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Keeps the combat sun where combat put it. Fixes a client's battlefield
    /// rendering flat and shadowless while its host's is lit.
    /// </summary>
    /// <remarks>
    /// <b>Two systems write the same field and the last one wins.</b>
    /// <list type="bullet">
    /// <item><c>TimeSyncSystem</c> (combat) is reactive on
    /// <c>CombatMatcher.TimeOfDay</c> and sets
    /// <c>TOD_Sky.Instance.Cycle.Hour = combat.timeOfDay.f</c>.</item>
    /// <item><c>OverworldSkySystem</c> is reactive on
    /// <c>OverworldMatcher.TimeOfDay</c> and sets the same
    /// <c>Cycle.Hour</c> from <c>overworld.timeOfDay.f</c>.</item>
    /// </list>
    /// On a host the combat write is the last one, because a host reaches
    /// combat from the overworld and nothing touches the overworld clock
    /// afterwards. <b>A client reaches combat by loading a save</b>, and that
    /// load writes the overworld clock again — after the combat environment is
    /// already up — so the overworld system fires last and drags the sun back
    /// to the campaign's hour.
    /// <para>
    /// <b>Measured on two real games, 2026-08-15.</b> Both machines agreed on
    /// every input: <c>overworld.timeOfDay</c> 20.906, <c>combat.timeOfDay</c>
    /// 18.800, sunset 20.386, identical graphics settings, same area, same
    /// biome. The only divergence was the sun itself — host
    /// <c>TOD_Sky.cycleHour</c> <b>18.800</b> (evening, sun up, long shadows)
    /// against client <b>20.906</b> (past sunset, so no directional light at
    /// all), with the client's ambient correspondingly boosted to night levels,
    /// 1.72 against 1.06. The user described it exactly: the client "loads with
    /// the shadows for a split second before it all gets blown away flat".
    /// </para>
    /// <para>
    /// <b>A postfix that corrects, rather than a prefix that suppresses.</b>
    /// <c>OverworldSkySystem.Execute</c> also drives the time-of-day music sync
    /// and refreshes ambient and reflection, and none of that deserves to be
    /// cancelled to fix the sun. So the system runs in full and this puts the
    /// one field it should not own during a fight back where combat set it.
    /// </para>
    /// <para>
    /// Not client-specific, deliberately. It reads game state and the combat
    /// clock, never the session — so it is equally correct on a host whose
    /// overworld clock moves mid-fight, and it cannot rot when the session
    /// model changes. It is also arguably a fix to a vanilla bug that only
    /// something loading a save straight into combat can reach.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(OverworldSkySystem), "Execute", new[] { typeof(List<OverworldEntity>) })]
    internal static class SkyPatches
    {
        [HarmonyPostfix]
        private static void RestoreCombatSun()
        {
            // The overworld sky is only wrong while a fight is on screen.
            // Outside combat this system is the authority and must be left be.
            if (!IDUtility.IsGameState("combat"))
            {
                return;
            }

            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasTimeOfDay)
            {
                return;
            }

            var sky = TOD_Sky.Instance;
            if (sky == null)
            {
                return;
            }

            sky.Cycle.Hour = combat.timeOfDay.f;

            // Ambient and reflection are baked from the sun's position, so
            // moving it back is not enough on its own — the overworld system
            // may already have refreshed them against the wrong hour, or be
            // about to. Deferred by two frames exactly as TimeSyncSystem does
            // it, which is the combat path this is restoring.
            Co.DelayFrames(2, delegate
            {
                var current = TOD_Sky.Instance;
                if (current != null)
                {
                    current.UpdateAmbient();
                    current.UpdateReflection();
                }
            });
        }
    }
}
