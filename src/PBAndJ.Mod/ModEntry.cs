using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core;
using PhantomBrigade;
using PhantomBrigade.Mods;
using UnityEngine;

namespace PBAndJ.Mod
{
    // Humble-object glue: executes only inside the game runtime, contains no
    // logic (all composition lives in PBAndJ.Core, which is under the 100%
    // coverage gate). Verified by the in-game smoke checklist instead.
    [ExcludeFromCodeCoverage]
    public class PBAndJModLink : ModLink
    {
        public override void OnLoadStart()
        {
            Debug.Log(LoadBanner.Compose(modID, metadata.ver));
        }

        public override void OnLoadEnd()
        {
            base.OnLoadEnd();
            ActionDumpGlue.RegisterConsoleCommand();
            SaveLoadGlue.RegisterConsoleCommands();
            SaveLoadGlue.EnableCombatSaves();
            InjectGlue.RegisterConsoleCommand();
            Net.NetGlue.RegisterConsoleCommands();
            UpdateGlue.RegisterConsoleCommand();
            // THROWAWAY (M12 recon) — goes when docs/notes/overworld-recon.md is written.
            Net.OverworldProbeGlue.RegisterConsoleCommands();
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "Start")]
    internal static class Patch_Heartbeat_Start
    {
        // Heartbeat is a MonoBehaviour that lives for the whole process, so it
        // is also the coroutine host the update check needs — UnityWebRequest
        // requires one and the mod has none of its own. Borrowing this is
        // cheaper and less fragile than creating and owning a GameObject.
        private static void Postfix(Heartbeat __instance)
        {
            Debug.Log(LoadBanner.PatchFired("Heartbeat.Start"));
            UpdateGlue.SetCoroutineHost(__instance);
            Net.OverworldProbeGlue.SetCoroutineHost(__instance);
        }
    }
}
