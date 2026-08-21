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
    // Hooks the game-side patches and glue call when something has happened
    // locally. Every one returns void, so no caller can come to depend on the
    // session's answer -- and NotifyExternalTurnAdvance posts nothing at all: it
    // only records that a turn moved without going through the barrier.
    internal static partial class NetGlue
    {
        /// <summary>Reports a finished load back into the session. M11d.</summary>
        internal static void PostLoadFinished(int selectionVersion, LoadOutcome outcome) =>
            runtime?.Post(new LoadFinishedEvent(selectionVersion, outcome));

        /// <summary>The fight is written and can be offered. Host only. M12b.</summary>
        internal static void PostLocalCombatReady(string? saveName, string? digest) =>
            runtime?.Post(new LocalCombatReadyEvent(saveName, digest));

        /// <summary>Reports how joining the host's fight went. Client only. M12b.</summary>
        internal static void PostCombatLoadFinished(LoadOutcome outcome) =>
            runtime?.Post(new CombatLoadFinishedEvent(outcome));

        /// <summary>Tells the session where our base is, for M12a's mirror.</summary>
        internal static void PostLocalBasePosition(float x, float z) =>
            runtime?.Post(new LocalBasePositionEvent(x, z));

        internal static void PostLocalReady()
        {
            runtime?.Post(new LocalReadyEvent());
        }

        internal static void PostLocalTurnComplete()
        {
            if (runtime == null || bridge == null)
            {
                return;
            }
            // One capture, then the digest projected from it — so the digest
            // describes exactly the state that goes on the wire. Reading the
            // bridge twice, or building a throwaway one as this used to, would
            // let the two drift apart between calls.
            //
            // Keyframes are read in the same call for the same reason, and it is
            // load-bearing here: the final key capture appends comes from the
            // same read the snapshot does, which is what makes "playback ends
            // where the correction put it" true rather than hoped for.
            var snapshot = bridge.CaptureSnapshot();
            var keyframes = bridge.CaptureKeyframes();
            lastCapture = keyframes;
            lastCaptureTurn = bridge.CurrentTurn;
            runtime.Post(new LocalTurnCompleteEvent(
                bridge.ComputeStateDigest(), snapshot, keyframes));
        }

        /// <summary>
        /// A turn advanced without going through the barrier — scenario content
        /// calling CombatForceExecution, or the debug console. The host treats
        /// it as authoritative rather than fighting it.
        /// </summary>
        internal static void NotifyExternalTurnAdvance(int from, int to)
        {
            if (runtime?.Session is HostSession)
            {
                Debug.Log($"[pb-and-j] turn advanced outside the barrier ({from} -> {to}) — scenario or console");
            }
        }
    }
}
