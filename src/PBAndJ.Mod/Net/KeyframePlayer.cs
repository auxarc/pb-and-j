using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Presents a received turn's motion. Humble-object glue: every decision
    // about *where* a unit should be at a given moment lives in
    // KeyframePlayback, under the coverage gate; this only resolves entities and
    // writes transforms.
    //
    // It writes the VIEW transform, never the ECS position:
    //
    //   * It keeps playback genuinely presentational. ECS position feeds order
    //     authoring, scenario state volumes and the state digest, so animating
    //     it sixty times a second would let a player author orders from
    //     historical positions and would put a half-played animation into the
    //     correction check. This is the same call the game's own replay scrubber
    //     makes (ApplyTimeToUnit writes view.transform directly).
    //
    //   * It self-heals. PositionLinkSystem is reactive on CombatMatcher.Position
    //     and simply calls CombatView.OnPosition, which sets transform.position —
    //     so the next ReplacePosition on a unit, from execution or from the next
    //     snapshot correction, snaps its view straight back to ECS truth. An
    //     abandoned playback cannot leave anything permanently displaced.
    //
    // The handle is combatView, NOT transformLink: CombatEntity.ReplaceTransformLink
    // is never called anywhere in the game, so no unit has that component and
    // TransformLinkSystem never sees one. Filtering on it silently matched zero
    // units.
    //
    // We deliberately do NOT drive CombatReplayHelper's own scrubber: its
    // activation path sleeps puppets and disables ragdoll physics, and is gated
    // behind IsReplayAllowed() — scenario replayUsed, an unlocked feature and a
    // particular UI mode — none of which a client can assume.
    [ExcludeFromCodeCoverage]
    internal static class KeyframePlayer
    {
        private sealed class Target
        {
            public Target(UnitTrack track, Transform transform)
            {
                Track = track;
                Transform = transform;
            }

            public UnitTrack Track { get; }
            public Transform Transform { get; }
        }

        private static readonly List<Target> targets = new List<Target>();
        private static float windowStart;
        private static float windowEnd;
        private static float cursor;
        private static bool playing;

        internal static bool IsPlaying => playing;

        /// <summary>The turn currently being presented, or -1.</summary>
        internal static int Turn { get; private set; } = -1;

        internal static void Play(int turn, KeyframeCapture capture)
        {
            Stop();

            if (capture.Tracks.Count == 0 || !IDUtility.IsGameState("combat"))
            {
                return;
            }

            // Resolve once, not per frame. Unity's null check covers an entity
            // destroyed mid-playback, so a stale Transform simply stops moving.
            var byName = new Dictionary<string, UnitTrack>(capture.Tracks.Count);
            foreach (var track in capture.Tracks)
            {
                if (!string.IsNullOrEmpty(track.Name))
                {
                    byName[track.Name!] = track;
                }
            }

            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                if (!unit.hasCombatView || unit.combatView.view == null)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                if (byName.TryGetValue(persistent.nameInternal.s, out var track))
                {
                    targets.Add(new Target(track, unit.combatView.view.transform));
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            windowStart = capture.WindowStart;
            windowEnd = capture.WindowEnd;
            cursor = capture.WindowStart;
            Turn = turn;
            playing = true;
        }

        internal static void Stop()
        {
            targets.Clear();
            playing = false;
            Turn = -1;
        }

        /// <summary>Pumped from the same Heartbeat postfix the runtime is.</summary>
        internal static void Advance(float deltaSeconds)
        {
            if (!playing)
            {
                return;
            }

            // Real time against simulation time one-for-one: the host recorded
            // the turn at the rate it was simulated, so replaying it at any other
            // rate would be a different turn.
            cursor += deltaSeconds;
            var finished = cursor >= windowEnd;
            if (finished)
            {
                cursor = windowEnd;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target.Transform == null)
                {
                    continue;
                }
                if (!KeyframePlayback.TrySample(target.Track, cursor, out var position, out var rotation))
                {
                    continue;
                }
                target.Transform.position = new Vector3(position.X, position.Y, position.Z);
                target.Transform.rotation = new Quaternion(
                    rotation.X, rotation.Y, rotation.Z, rotation.W);
            }

            if (finished)
            {
                Stop();
            }
        }

        /// <summary>Only for the log line — the window this playback covers.</summary>
        internal static float Duration => windowEnd - windowStart;
    }
}
