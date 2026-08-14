using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat.View;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Presents a received turn's motion. Humble-object glue: every decision
    // about *where* a unit or a joint should be at a given moment lives in
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
    // M8 adds the skeleton. We deliberately do NOT drive CombatReplayHelper's own
    // scrubber, for two reasons that only became clear on a full reading:
    //
    //   * ApplyTime iterates CombatReplayHelper.units, and that dictionary is
    //     filled only by OnExecutionStart — which runs off the ECS Simulating
    //     flag a client never gains. On a client it is empty, so driving the
    //     game's scrubber would mean fabricating its own ReplayUnit graph into a
    //     static two systems are documented to wipe.
    //
    //   * It would not even save us the work below. SleepPuppet is reachable
    //     only through SetReplayActive, which a client cannot call
    //     (activationAllowed is set in OnExecutionEnd and stays false forever
    //     here) — so the game's scrubber would have its bone writes overwritten
    //     by the client's own idle animation exactly as ours would.
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

            /// <summary>Null when this unit plays transform-only.</summary>
            public UnitPoseTrack? Poses { get; set; }

            /// <summary>This machine's own recorded bones, in its own order.</summary>
            public List<Transform>? Bones { get; set; }

            /// <summary>Client bone index to host joint index, or -1.</summary>
            public int[]? Remap { get; set; }

            /// <summary>Present only on mechs, and only for the palm sync.</summary>
            public CombatMechAnimationView? MechView { get; set; }
        }

        /// <summary>
        /// One unit we put to sleep, and what has to be undone.
        /// </summary>
        /// <remarks>
        /// Kept in its own list rather than read back off <see cref="Target"/>,
        /// because the unwind must not need the tracks to know what it slept.
        /// Combat teardown empties the very collections a track-driven unwind
        /// would iterate, and a unit left with a disabled animator is a statue
        /// for the rest of the session.
        /// <para>
        /// It is also why the game's own <c>puppetPhysicsMap</c> is untouched:
        /// <c>DisableRagdollPhysics</c> stores into it only past an early
        /// return that <c>EnableRagdollPhysics</c> does not repeat before
        /// indexing it, so a unit whose crash state changes mid-window throws
        /// <c>KeyNotFoundException</c> on wake. We skip that half entirely and
        /// keep our own record of what we touched.
        /// </para>
        /// </remarks>
        private sealed class Sleeper
        {
            public Sleeper(CombatMechAnimationView view)
            {
                View = view;
            }

            public CombatMechAnimationView View { get; }
            public GameObject? FullBodyIk { get; set; }
            public GameObject? PuppetMaster { get; set; }
            public GameObject? PuppetBehaviour { get; set; }
        }

        private static readonly List<Target> targets = new List<Target>();
        private static readonly List<Sleeper> sleepers = new List<Sleeper>();
        private static float windowStart;
        private static float windowEnd;
        private static float cursor;
        private static bool playing;

        internal static bool IsPlaying => playing;

        /// <summary>The turn currently being presented, or -1.</summary>
        internal static int Turn { get; private set; } = -1;

        /// <summary>How many units are playing with skeletal animation.</summary>
        internal static int PosedUnits { get; private set; }

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

            var posesByName = new Dictionary<string, UnitPoseTrack>(capture.Poses.Count);
            foreach (var pose in capture.Poses)
            {
                if (!string.IsNullOrEmpty(pose.Name))
                {
                    posesByName[pose.Name!] = pose;
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
                if (!byName.TryGetValue(persistent.nameInternal.s, out var track))
                {
                    continue;
                }

                var target = new Target(track, unit.combatView.view.transform);
                targets.Add(target);

                if (posesByName.TryGetValue(persistent.nameInternal.s, out var pose))
                {
                    Dress(unit, target, pose);
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

        /// <summary>
        /// Binds one unit's pose track to this machine's own skeleton, and puts
        /// its animation to sleep so the bones stay where we write them.
        /// </summary>
        /// <remarks>
        /// A unit we cannot dress is left transform-only rather than taken as a
        /// reason to demote the whole turn. That is deliberate and it is the
        /// host's own rule arriving here intact: the host already drops a track
        /// too short to animate, on the grounds that its own replay does not
        /// animate it either. Demoting everyone because one unit stood still
        /// would take poses away from the units that have them.
        /// <para>
        /// Sleep is applied here, before the first frame of playback, not
        /// alongside it. A save load schedules forced animation ticks half a
        /// second and two and a half seconds later that bypass the
        /// <c>Time.timeScale</c> gate everything else here relies on, so the
        /// window between resolving a unit and silencing it has to be zero.
        /// </para>
        /// </remarks>
        private static void Dress(CombatEntity unit, Target target, UnitPoseTrack pose)
        {
            var visualManager = unit.combatView.view.visualManager;
            var bones = visualManager != null ? visualManager.GetRecordedBones() : null;
            if (bones == null || bones.Count == 0)
            {
                return;
            }

            target.Poses = pose;
            target.Bones = bones;
            target.Remap = PoseTracks.Remap(pose.Joints, NamesOf(bones));
            PosedUnits++;

            if (!unit.hasMechAnimationView)
            {
                // Tanks and the like: three recorded bones, driven by nothing
                // that needs silencing. The game does not sleep them either.
                return;
            }

            var mechView = unit.mechAnimationView.view;
            if (mechView == null)
            {
                return;
            }
            target.MechView = mechView;

            var sleeper = new Sleeper(mechView);
            sleepers.Add(sleeper);

            // Order matters on the way down: pauseUpdates last, so nothing
            // reaches a half-silenced unit. It is load-bearing rather than
            // insurance — it is the only switch that closes both of
            // MechAnimationSystem's entry points, including the post-load
            // forced ticks that ignore Time.timeScale.
            if (mechView.animator != null)
            {
                mechView.animator.enabled = false;
            }
            if (mechView.ikFullBodyIK != null)
            {
                sleeper.FullBodyIk = mechView.ikFullBodyIK.gameObject;
                sleeper.FullBodyIk.SetActive(false);
            }

            // PuppetMaster maps muscle transforms onto these same bones from
            // its own LateUpdate, with no timeScale gate at all. A functional
            // mech is kinematic and blends at zero, so it costs nothing there —
            // but a crashed or wrecked one is Active and would overwrite every
            // bone we write, every frame. Deactivating the two holders is what
            // vanilla does, and unlike the ragdoll physics map it stores nothing
            // that a changed unit state can make unrecoverable.
            if (unit.hasPuppetView && unit.puppetView.view != null)
            {
                var puppetView = unit.puppetView.view;
                if (puppetView.puppetMaster != null)
                {
                    sleeper.PuppetMaster = puppetView.puppetMaster.gameObject;
                    sleeper.PuppetMaster.SetActive(false);
                }
                if (puppetView.puppetBehaviour != null)
                {
                    sleeper.PuppetBehaviour = puppetView.puppetBehaviour.gameObject;
                    sleeper.PuppetBehaviour.SetActive(false);
                }
            }

            mechView.pauseUpdates = true;
        }

        private static IReadOnlyList<string> NamesOf(List<Transform> bones)
        {
            var names = new string[bones.Count];
            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                names[i] = bone != null ? bone.name : string.Empty;
            }
            return names;
        }

        internal static void Stop()
        {
            Wake();
            targets.Clear();
            PosedUnits = 0;
            playing = false;
            Turn = -1;
        }

        /// <summary>
        /// Undoes every sleep, in the reverse of the order it was applied.
        /// </summary>
        /// <remarks>
        /// Every handle is null-checked on the way back up, because the unwind
        /// runs on paths where a unit may have been destroyed since: combat
        /// end, a session fault, a peer saying goodbye, and a turn commit that
        /// lands mid-window.
        /// </remarks>
        private static void Wake()
        {
            for (var i = 0; i < sleepers.Count; i++)
            {
                var sleeper = sleepers[i];
                if (sleeper.View != null)
                {
                    sleeper.View.pauseUpdates = false;
                }
                if (sleeper.PuppetBehaviour != null)
                {
                    sleeper.PuppetBehaviour.SetActive(true);
                }
                if (sleeper.PuppetMaster != null)
                {
                    sleeper.PuppetMaster.SetActive(true);
                }
                if (sleeper.FullBodyIk != null)
                {
                    sleeper.FullBodyIk.SetActive(true);
                }
                if (sleeper.View != null && sleeper.View.animator != null)
                {
                    sleeper.View.animator.enabled = true;
                }
            }
            sleepers.Clear();
        }

        /// <summary>Pumped from the same Heartbeat postfix the runtime is.</summary>
        /// <remarks>
        /// Everything is inside a catch, and the catch stops playback rather
        /// than swallowing and continuing. A driver that throws mid-window with
        /// units asleep would leave them frozen with their animators off for the
        /// rest of the session — the unwind is the thing that must not be
        /// skipped, whatever went wrong above it.
        /// </remarks>
        internal static void Advance(float deltaSeconds)
        {
            if (!playing)
            {
                return;
            }

            try
            {
                Step(deltaSeconds);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[pb-and-j] replay driver failed, stopping playback: "
                        + e.GetType().Name + ": " + e.Message);
                Stop();
            }
        }

        private static void Step(float deltaSeconds)
        {
            // Leaving combat with units asleep would strand them mid-pose with
            // their animators off for the rest of the session, and the effects
            // that stop playback do not all fire on every exit.
            if (!IDUtility.IsGameState("combat"))
            {
                Stop();
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
                if (KeyframePlayback.TrySample(target.Track, cursor, out var position, out var rotation))
                {
                    target.Transform.position = new Vector3(position.X, position.Y, position.Z);
                    target.Transform.rotation = new Quaternion(
                        rotation.X, rotation.Y, rotation.Z, rotation.W);
                }

                ApplyPose(target);
            }

            if (finished)
            {
                Stop();
            }
        }

        /// <summary>
        /// Writes one unit's skeleton for the current cursor position.
        /// </summary>
        /// <remarks>
        /// Local space throughout, so it is independent of the root transform
        /// written just above — and the palm sync, which is world space, comes
        /// after both for that reason.
        /// <para>
        /// The bone count is re-checked every frame rather than trusted from
        /// install time. <c>UnitVisualManagerSimple.RefreshRecordedBones</c>
        /// clears and rebuilds its list when a view is re-created, and a remap
        /// built against the old one would write the wrong bones or index past
        /// the end of the new one. Mechs never rebuild — theirs is guarded by an
        /// initialised flag — but this driver poses whatever has bones.
        /// </para>
        /// </remarks>
        private static void ApplyPose(Target target)
        {
            var bones = target.Bones;
            var remap = target.Remap;
            if (bones == null || remap == null || bones.Count != remap.Length)
            {
                return;
            }
            if (!KeyframePlayback.TryBracket(target.Poses, cursor, out var span))
            {
                return;
            }

            for (var i = 0; i < remap.Length; i++)
            {
                var source = remap[i];
                if (source == PoseTracks.NoSource)
                {
                    // A bone the host never recorded. Left exactly where it is,
                    // which — the animator being asleep — is the pose it held
                    // when playback started. That is the same filler the game's
                    // own restore path uses, and it costs nothing to obtain.
                    continue;
                }
                var bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                KeyframePlayback.SampleJoint(span, source, out var position, out var rotation);
                bone.localPosition = new Vector3(position.X, position.Y, position.Z);
                bone.localRotation = new Quaternion(
                    rotation.X, rotation.Y, rotation.Z, rotation.W);
            }

            SyncEquipment(target, span);
        }

        /// <summary>
        /// Pins a weapon to the palm joint it is being held by.
        /// </summary>
        /// <remarks>
        /// The two flags are the reason a pose is not merely a time and an array
        /// of joints. Without this the rifle floats off the hand through every
        /// firing animation — the milestone's showcase, broken in its showcase
        /// moment. World space, and therefore after the bone writes, exactly as
        /// the game orders it.
        /// </remarks>
        private static void SyncEquipment(Target target, PoseSpan span)
        {
            var view = target.MechView;
            if (view == null)
            {
                return;
            }

            if (span.SyncLeftEquipment
                && view.lWeaponTransform != null && view.lWeaponJointPalmLocal != null)
            {
                view.lWeaponTransform.position = view.lWeaponJointPalmLocal.position;
                view.lWeaponTransform.rotation = view.lWeaponJointPalmLocal.rotation;
            }
            if (span.SyncRightEquipment
                && view.rWeaponTransform != null && view.rWeaponJointPalmLocal != null)
            {
                view.rWeaponTransform.position = view.rWeaponJointPalmLocal.position;
                view.rWeaponTransform.rotation = view.rWeaponJointPalmLocal.rotation;
            }
        }

        /// <summary>Only for the log line — the window this playback covers.</summary>
        internal static float Duration => windowEnd - windowStart;
    }
}
