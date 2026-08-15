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
            // The game owns this string — it is what it passed to `new Harmony(id)`
            // — so it is captured rather than copied, and pbj.drive-state can
            // report how many methods we actually patched.
            Net.ActuatorGlue.RememberHarmonyId(modID);
        }

        public override void OnLoadEnd()
        {
            base.OnLoadEnd();
            ActionDumpGlue.RegisterConsoleCommand();
            SaveLoadGlue.RegisterConsoleCommands();
            SaveLoadGlue.EnableCombatSaves();
            InjectGlue.RegisterConsoleCommand();
            Net.NetGlue.RegisterConsoleCommands();
            Net.ActuatorGlue.RegisterConsoleCommands();
            UpdateGlue.RegisterConsoleCommand();
#if PBJ_DRIVE
            // Started from here rather than from a patch, and the coupling is
            // deliberate: OnLoadEnd runs only if PatchAll succeeded, and the
            // channel's pump is a Harmony postfix. Starting it anywhere earlier
            // could leave a socket accepting commands that nothing would ever
            // execute. No port set means no listener.
            Net.DriveProbeGlue.RegisterConsoleCommand();
            Net.DriveGlue.Start();
#endif
            // THROWAWAY (M12 recon) — goes when docs/notes/overworld-recon.md is written.
            Net.OverworldProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M12 review follow-up) — answers the two questions gating
            // M12d. Goes once both answers are in the design doc.
            Net.ManagementProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M8 recon) — four of its five questions are answered in
            // docs/notes/replay-handoff-recon.md. It stays only until the fifth,
            // a client's Time.timeScale during playback, is read off a running
            // client; then it goes the way of the other probes.
            Net.ReplayProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M13) — it answered why a client drew "no data" over a
            // unit the host drew normally, and that answer is now in
            // docs/notes/replay-handoff-recon.md. It stays only for the question
            // that pass left open: a revealed unit's ArrivalTime reads -1 on a
            // client against the host's real value. Goes with that one.
            Net.OverlayProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M8 leftovers) — when a unit was visible during a turn,
            // and where that answer comes from. Four rounds of reading the
            // decompile produced four accounts of this mechanism and three were
            // wrong; the arguments are all about facts one line of output
            // settles. Goes once the answers are in the recon file.
            Net.VisibilityProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M8 item 3 recon) — §8 of the recon file says the VFX
            // volume "has never been measured", and designing a wire around an
            // unmeasured volume is how M6 nearly shipped a frame the receiver
            // would have rejected. Goes once the number is in the recon file.
            Net.VfxProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (enemy previews recon) — whether the enemy's plan on a
            // client is the host's, the client's own, or nothing at all. Three
            // outcomes that look identical in a log and completely different on
            // screen, so it must be run on both machines and diffed. Goes once
            // the answer is in docs/notes/enemy-previews-recon.md.
            Net.ActionProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M14 revision 4) — the one open question that could
            // still reshape M14: whether a client can render a replayed pooled
            // effect, and whether the frozen _TimeSimulation global spoils it.
            // Unknowable from source — the shaders are compiled assets.
            Net.TimeSimProbeGlue.RegisterConsoleCommands();
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
            Net.BaseMirrorGlue.SetCoroutineHost(__instance);
        }
    }
}
