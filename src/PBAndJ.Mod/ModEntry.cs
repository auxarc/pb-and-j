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
            // THROWAWAY (M12 recon). Its own header sets the bar higher than
            // this line used to: "delete it once EVERY finding is in
            // docs/notes/overworld-recon.md". That file is written but not
            // complete — the nightfall/flat-lighting chain ProbeNightfall reads
            // appears nowhere in docs/, and the notes' own "⏳ Still unrun"
            // section leaves measurements 2, 5 and 6 to the two-instance rig,
            // for which this file is the instrument. Swept 2026-08-21: KEPT.
            Net.OverworldProbeGlue.RegisterConsoleCommands();
            // THROWAWAY (M12 review follow-up) — answers the two questions gating
            // M12d. Goes once both answers are in the design doc.
            Net.ManagementProbeGlue.RegisterConsoleCommands();
            // Was THROWAWAY (M8 recon), and its stated condition is now MET: all
            // six questions, the fifth included, are answered in
            // docs/notes/replay-handoff-recon.md — a client's Time.timeScale was
            // read across 577 playback frames and never left zero. What keeps
            // the file alive is something the header never mentions: it also
            // registers pbj.pose-digest, M18's live instrument over
            // KeyframePlayer.DigestPose and Core's PoseDigest. Swept 2026-08-21:
            // KEPT for that, not for the recon. Move pbj.pose-digest to a
            // non-throwaway glue file and the shell can go.
            Net.ReplayProbeGlue.RegisterConsoleCommands();
            // ⚠️ NOT throwaway any more, whatever this line used to say. The
            // volume number it was written for IS in docs/notes/replay-handoff-
            // recon.md now ("VFX VOLUME MEASURED AT LAST"), so on the old
            // wording it would already be due for deletion — and deleting it
            // would take the `presimulated` counter inside pbj.vfx-probe, which
            // is BLOCKED on a reading from a running game and decides whether
            // advanced particle blocks are a feature or are cut. The rest
            // (fx-instances, fx-pools, fx-tsim, fx-mirror, fx-hold) is on the
            // standing keep list. Swept 2026-08-21: KEPT WHOLE, do not sweep
            // again until that reading exists.
            Net.VfxProbeGlue.RegisterConsoleCommands();
            Net.BeamInjectGlue.RegisterConsoleCommands();
            Net.StageCProbeGlue.RegisterConsoleCommands();
            Net.DestructProbeGlue.RegisterConsoleCommands();
            // RIG INSTRUMENTS for the 1.0 comprehensive run. Their sweep
            // conditions are stated in docs/notes/rig-run-1-0.md as TESTS
            // ("is reading N recorded with its numbers?"), never as an
            // observation a later grep can make come true by being written
            // down. Swept 2026-08-21: NEW, KEPT.
            Net.DebriefProbeGlue.RegisterConsoleCommands();
            Net.CombatEdgeProbeGlue.RegisterConsoleCommands();
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
