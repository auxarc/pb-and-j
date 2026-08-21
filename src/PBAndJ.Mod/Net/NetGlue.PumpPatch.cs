using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Content.Code.Utility;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PBAndJ.Core.Net;
using PBAndJ.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The Harmony patch that drives the pump. Its own file because it is its own
    // type: the [HarmonyPatch] attribute above the declaration is what makes it
    // apply at all.

    // The pump site: Heartbeat.Update is the main thread in every game state,
    // including the main menu where the lobby must work and no Entitas combat
    // system exists. It is also where SteamHelper.RunCallbacks already lives, so
    // a future Steam transport pumps from the identical place.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "Update")]
    internal static class Patch_Heartbeat_Update
    {
        private static void Postfix()
        {
            NetGlue.Pump();

            // Immediately after the pump, and that ordering is the point: the
            // pump runs its effects synchronously, so a ShipCombatEffect raised
            // by this frame's combat edge has already armed the glue by now. The
            // poll can never run ahead of the session that asked for it.
            CombatShipGlue.Tick();

            // Outside the pump's session guard on purpose: pbj.replay-last has to
            // work on a host with no session, and playback must keep running for
            // its full window even if the session faults halfway through.
            // Unscaled, because the game parks Time.timeScale at zero during
            // planning.
            KeyframePlayer.Advance(Time.unscaledDeltaTime);

            // M14 measurement 2's beam injector. Outside the session guard for
            // the same reason playback is: it has to work on a solo host, which
            // is where a turn carrying a beam is authored in the first place.
            BeamInjectGlue.Tick();
            StageCProbeGlue.Tick();

            // Was THROWAWAY (M8 recon). ⚠️ ANSWERED: 577 client frames, zero on
            // every one — docs/notes/replay-handoff-recon.md. Now a standing
            // FinalIK watch; goes when pbj.pose-digest leaves ReplayProbeGlue.
            ReplayProbeGlue.SampleDuringPlayback();

            // Also outside the session guard: the connect screen exists in order
            // to start a session, so it must run when there is none.
            ConnectScreenGlue.Tick();
            LobbyScreenGlue.Tick();

            // Step 0 of the drive rig, and it runs from here rather than from a
            // console command on purpose: the question is whether Quantum
            // Console works when nobody has opened it, and asking through a
            // console command would answer a different one. Self-disarming after
            // a single run.
#if PBJ_DRIVE
            DriveProbeGlue.Tick();

            // One queued drive command per frame. Last, so a driven command
            // observes the state this frame's pump and glue have already
            // settled, rather than a half-updated one.
            DriveGlue.Tick();
#endif
        }
    }
}
