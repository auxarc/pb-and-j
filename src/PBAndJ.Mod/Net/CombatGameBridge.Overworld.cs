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
    // MirrorBase, and it is the only member of this class that touches the overworld
    // rather than a combat.
    //
    // It has a file to itself for that reason: the alternative was to put an
    // overworld base teleport in the keyframe playback file, where its own header
    // would not have covered it.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        /// <summary>
        /// Puts this machine's mobile base where the host's is. M12a.
        /// </summary>
        /// <remarks>
        /// The game's own teleport recipe, cribbed from
        /// <c>ConsoleCommandsOverworld:893-901</c> and proven by <c>pbj.ow-mirror</c>
        /// during the recon rather than invented here. Every step earns its
        /// place, and the recipe is the whole reason this is not two lines:
        /// <list type="bullet">
        ///   <item><c>StopMovement</c> — or the client's own path fights the write.</item>
        ///   <item><c>ReplacePosition</c> — the authoritative value.</item>
        ///   <item><c>ReplacePositionTarget</c> — <b>not optional.</b>
        ///   <c>OverworldMovementSystem</c> drags position back toward a stale
        ///   target whenever the clock runs, so a mirror without it snaps back.</item>
        ///   <item><c>isPositionUnchecked</c> — hands the height to
        ///   <c>OverworldPositionValidationSystem</c>, which snaps to this
        ///   machine's own ground. That is why no Y crosses the wire.</item>
        ///   <item>A <b>same-value</b> <c>ReplaceSimulationTime</c> — Entitas
        ///   raises the replaced event with no value-equality short-circuit, so
        ///   this wakes every <c>SimulationTime</c> collector at a delta of zero.
        ///   <c>OverworldRangeSystem</c> is the one that matters: it copies
        ///   Position into PositionDetectedLast, which is what the renderer
        ///   actually draws.</item>
        /// </list>
        /// <b>Never write the host's time value here.</b> Roughly twenty systems
        /// collect on that component and a real delta would run all of them on a
        /// machine that is not simulating — the overworld cousin of the standing
        /// rule against advancing <c>combat.simulationTime</c> on a client.
        /// <para>
        /// In game state <c>basecrawler</c> the write lands and does not render,
        /// because the feeder above runs only in <c>overworld</c>. That is
        /// measured-correct, not a bug to work around: the position is already
        /// right when the player returns to the map.
        /// </para>
        /// </remarks>
        public void MirrorBase(float x, float z)
        {
            var playerBase = IDUtility.playerBaseOverworld;
            if (playerBase == null || !playerBase.hasPosition)
            {
                return;
            }

            // Keep our own Y. The snap below corrects it against local ground,
            // and starting from the current height means an unremarkable
            // correction rather than a fall from wherever the host stands.
            var target = new Vector3(x, playerBase.position.v.y, z);

            PhantomBrigade.Overworld.OverworldUtility.StopMovement(playerBase);
            playerBase.ReplacePosition(target);
            playerBase.ReplacePositionTarget(target);
            playerBase.isPositionUnchecked = true;

            var overworld = Contexts.sharedInstance.overworld;
            if (overworld.hasSimulationTime)
            {
                overworld.ReplaceSimulationTime(overworld.simulationTime.f);
            }
        }
    }
}
