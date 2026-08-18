using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Combat.View;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Binding a skeleton, silencing it, and handing it back.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
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

            // Above the bones early-return, not below it. Reaction pings and
            // melee swings need neither bones nor a remap, and the game's own
            // replay drives them for any unit with a light manager at all
            // (CombatReplayHelper.cs:1245). Assigning these after the return
            // would silently confine both features to units this machine can
            // pose, which is a narrower set than the one the host recorded.
            //
            // Cached above the mech/tank fork for the same class of reason. Both
            // visual managers implement GetLightManager (UnitVisualManager:1720
            // and UnitVisualManagerSimple:516), so putting it after the tank
            // early-return below would quietly give lights to mechs only.
            target.Poses = pose;
            target.Lights = visualManager != null ? visualManager.GetLightManager() : null;

            var bones = visualManager != null ? visualManager.GetRecordedBones() : null;
            if (bones == null || bones.Count == 0)
            {
                return;
            }

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

            var sleeper = new Sleeper(mechView, target.Name);
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

        /// <summary>
        /// Unwinds one unit's sleep ahead of the rest, and stops posing it.
        /// </summary>
        private static void WakeOne(VisibilityWatch watch)
        {
            var target = watch.Target;
            if (target == null)
            {
                return;
            }

            // Bones stop being written the moment the unit is invisible, so the
            // pose driver has nothing left to do for it.
            target.Poses = null;
            target.Bones = null;

            for (var i = sleepers.Count - 1; i >= 0; i--)
            {
                var sleeper = sleepers[i];
                if (sleeper.View != null && target.MechView != null
                    && ReferenceEquals(sleeper.View, target.MechView))
                {
                    // Through the split, not straight to the wake. This is the
                    // route a hidden wreck takes, and taking it unconditionally
                    // stood every one of them back up. M17 stage 1.
                    WakeOrFreeze(sleeper);
                    sleepers.RemoveAt(i);
                }
            }
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
                WakeOrFreeze(sleepers[i]);
            }
            sleepers.Clear();
        }

        /// <summary>
        /// Hands a unit back to its animator — unless the host has wrecked it,
        /// in which case it is left exactly as the window left it. M17 stage 1.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>The decision lives here rather than in <see cref="Wake"/>, and
        /// that placement is the fix rather than a detail of it.</b>
        /// <see cref="WakeOne"/> is a second, entirely separate route into
        /// <see cref="WakeSleeper"/>: a unit whose visibility changes mid-window
        /// is woken early and removed from <see cref="sleepers"/>, so it never
        /// reaches <see cref="Wake"/> at all. And it fires more often than it
        /// looks — <c>Show</c> compares against a <c>bool?</c> that starts null,
        /// so a unit <i>hidden at window start</i> counts as changed on its very
        /// first frame. A split written one level up would have left every
        /// hidden wreck standing back up, which is precisely the case the
        /// feature exists for.
        /// <para>
        /// Freezing is the <b>absence</b> of an action, not an extra one. The
        /// sleep already disabled the animator and the IK, deactivated the two
        /// puppet holders and set <c>pauseUpdates</c>; leaving all of it in place
        /// is what keeps the corpse in the pose the host recorded for it.
        /// </para>
        /// <para>
        /// ⚠️ Deliberately does <b>not</b> call
        /// <c>UnitUtilities.OnUnitNonfunctional</c>, which an earlier draft of
        /// this reached for as the host's own wreck call. It sets
        /// <c>PuppetMaster.mode = Active</c> / <c>state = Dead</c>, and
        /// <c>PuppetMaster.OnEnable</c> (<c>:423-431</c>) responds to exactly
        /// that pair by calling <c>ActivateRagdoll(kinematic: false)</c> and
        /// re-activating every behaviour GameObject. On a client the puppet is
        /// only ever <c>Alive</c>/<c>Kinematic</c>, so planting the host's state
        /// would arm live corpse physics on any later activation while
        /// protecting nothing — and it is also why the revival path below needs
        /// no <c>OnUnitGetUp</c> inverse: nothing was ever changed to invert.
        /// </para>
        /// </remarks>
        private static void WakeOrFreeze(Sleeper sleeper)
        {
            if (!string.IsNullOrEmpty(sleeper.Name)
                && destruction.IsUnitWrecked(sleeper.Name))
            {
                frozen[sleeper.Name!] = sleeper;
                return;
            }
            WakeSleeper(sleeper);
        }

        /// <summary>
        /// Hands a revived unit back to its animator. M17 stage 1.
        /// </summary>
        /// <remarks>
        /// The only exit from <see cref="frozen"/> that restores anything. A
        /// revived unit that stayed frozen would be a permanent statue, which is
        /// the same defect as the one this feature fixes with the sign flipped —
        /// and M15 verified revival across eight units, so the path is real.
        /// <para>
        /// Keyed by name rather than by the sleeper handle because the view can
        /// be rebuilt between the freeze and the revival, and the name is the
        /// wire's own join key on both machines.
        /// </para>
        /// </remarks>
        private static void Unfreeze(string? unit)
        {
            if (string.IsNullOrEmpty(unit) || !frozen.TryGetValue(unit!, out var sleeper))
            {
                return;
            }

            frozen.Remove(unit!);
            WakeSleeper(sleeper);
            Unfrozen++;
        }

        private static void WakeSleeper(Sleeper sleeper)
        {
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
    }
}
