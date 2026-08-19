using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The client end of a keyframe capture: start playing what the host sent, and
    // stop.
    //
    // StopKeyframes carries a long comment about ordering that is load-bearing --
    // read it before reordering anything in it.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        // A host never calls this: it simulates the turn rather than playing a
        // recording of one. Client only, per IPbjGameBridge.PlayKeyframes.
        public void PlayKeyframes(int turn, KeyframeCapture capture)
        {
            // Kept before playing, so a client can replay what it was told to
            // play. Otherwise pbj.replay-last is host-only and the one machine
            // whose playback is worth inspecting twice is the one that cannot.
            NetGlue.RememberPlayed(turn, capture);
            KeyframePlayer.Play(turn, capture);
        }

        public void StopKeyframes()
        {
            // 🔴 ORDER IS LOAD-BEARING, and in three ways rather than the two
            // documented below. Stop() runs the wake, and M17 stage 1 turned the
            // wake into a wake-or-freeze that asks the destruction state whether
            // each unit is a corpse. So Stop() must come BEFORE ClearDestruction
            // or every wreck is handed back to its animator and stands up on the
            // way out of the fight — silently, with no counter moving and no
            // test failing. DestructionStateTests covers the half of that
            // coupling it can see (the flag surviving SettleWindow and not
            // surviving Clear); this comment is the other half.
            KeyframePlayer.Stop();

            // M16, and BEFORE the clear below discards what it is holding. This
            // is the third settle path and it is not optional: SettleWindow has
            // one call site, on a window's natural finish, so a turn whose
            // keyframes never arrived — the mutual-destruction ending, where the
            // host legitimately sends none — has no window to settle and, here at
            // combat end, no next snapshot either. Without this the final turn's
            // damage would be discarded in exactly the fight that produced most
            // of it.
            KeyframePlayer.SettlePartIntegrity();

            // M15, and only here rather than in Stop itself. Every emitter of
            // this effect means the fight is over for this client — CombatEnd,
            // Bye, or a fault — while Stop also runs between the turns of a live
            // fight, where discarding the wrecked set would put every blown-off
            // limb back on during the planning phase.
            KeyframePlayer.ClearDestruction();
        }
    }
}
