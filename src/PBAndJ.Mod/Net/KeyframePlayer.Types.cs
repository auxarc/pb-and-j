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
    // The four view-handle types the driver caches at install.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
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

            /// <summary>
            /// This unit's light manager, cached at install like every other
            /// handle here.
            /// </summary>
            /// <remarks>
            /// Null is ordinary and not an error: the game itself null-checks
            /// the result of <c>GetLightManager</c> (<c>CombatView.cs:43-47</c>)
            /// even though <c>ActionRecordingSystem:55</c> does not.
            /// </remarks>
            public UnitLightManager? Lights { get; set; }

            /// <summary>
            /// The unit itself, for the melee shockwave drive.
            /// </summary>
            /// <remarks>
            /// The game's replay hands its own <c>CombatEntity</c> straight to
            /// <c>MeleeUtility.CheckOverlapsWithShockwave</c>, which is what
            /// selects whose trail moves — so this is the identity that matters,
            /// not the recorded <c>unitCombatID</c>, which we deliberately do
            /// not carry.
            /// </remarks>
            public CombatEntity? Unit { get; set; }

            /// <summary>
            /// The persistent entity's internal name — the key the destruction
            /// state is held under. M15.
            /// </summary>
            /// <remarks>
            /// Cached rather than re-resolved through <c>GetLinkedPersistentEntity</c>
            /// per frame, and kept even though <see cref="Track"/> carries the
            /// same string: a target exists only where a transform track did, so
            /// reading it off the track would tie the destruction drive to the
            /// one carrier this feature must not depend on.
            /// </remarks>
            public string? Name { get; set; }

            /// <summary>
            /// This unit's visual manager, for the per-part dissolve. M15.
            /// </summary>
            /// <remarks>
            /// The manager itself rather than <see cref="Lights"/>'s owner:
            /// <c>OnIntegrityChange</c> and <c>OnSocketDestructionChange</c> are
            /// on <c>IUnitVisualManager</c>, which both the mech and the tank
            /// implementations satisfy.
            /// </remarks>
            public IUnitVisualManager? Visuals { get; set; }
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
            public Sleeper(CombatMechAnimationView view, string? name)
            {
                View = view;
                Name = name;
            }

            public CombatMechAnimationView View { get; }

            /// <summary>
            /// The persistent entity's internal name — the wire's own join key.
            /// </summary>
            /// <remarks>
            /// Carried so the wake can ask <c>DestructionState</c> whether this
            /// unit is a corpse before handing its animator back. Cached at
            /// install like every other handle here, and for the same reason:
            /// the wake runs on paths where the entity may be gone.
            /// </remarks>
            public string? Name { get; }
            public GameObject? FullBodyIk { get; set; }
            public GameObject? PuppetMaster { get; set; }
            public GameObject? PuppetBehaviour { get; set; }
        }

        /// <summary>
        /// One unit whose visibility changed during the window, and everything
        /// needed to move it without re-reading the ECS.
        /// </summary>
        /// <remarks>
        /// The views and the id are cached at install for the same reason
        /// <see cref="Sleeper"/> caches its handles: the transition fires
        /// mid-window, on entities the snapshot may have just killed and that
        /// combat teardown races. Every handle is Unity-null-checked at use.
        /// <para>
        /// <b>Three views, not one.</b> The game's own scrubber touches only
        /// <c>combatView</c>, but it can afford to because
        /// <c>PrepareUnitForReplay</c> has already parked the projection and
        /// puppet views for every unit. We have no such sandbox — these are live
        /// views — so we do what the game's ECS path does instead
        /// (<c>VisibilityLinkSystem</c>) and what its unwind does
        /// (<c>RestoreUnitForExecution</c>): all three.
        /// </para>
        /// </remarks>
        private sealed class VisibilityWatch
        {
            public VisibilityWatch(int id, bool endHidden, float reveal, float hide)
            {
                Id = id;
                EndHidden = endHidden;
                Reveal = reveal;
                Hide = hide;
            }

            public int Id { get; }
            public bool EndHidden { get; }
            public float Reveal { get; }
            public float Hide { get; }

            public CombatView? View { get; set; }
            public ProjectedCombatView? Projected { get; set; }
            public CombatPuppetView? Puppet { get; set; }

            /// <summary>Null while this unit plays transform-only.</summary>
            public Target? Target { get; set; }

            /// <summary>What we last told the engine, or null before the first frame.</summary>
            public bool? Applied { get; set; }
        }

        /// <summary>
        /// One replayed effect: the game's own track, and the instance it holds.
        /// </summary>
        /// <remarks>
        /// The game's <c>ReplayEntityAsset</c> subclasses are reused verbatim
        /// rather than reimplemented — they are public, their fields are public,
        /// and their <c>ApplyTime</c> is the interpolation we would otherwise be
        /// copying by hand. This is the opposite of M8's finding for units,
        /// where driving the game's own playback was strictly more work; the
        /// asset half has no puppet, no animator and no sleep bookkeeping.
        /// </remarks>
        private sealed class AssetShow
        {
            public AssetShow(ReplayEntityAsset track)
            {
                Track = track;
            }

            public ReplayEntityAsset Track { get; }

            /// <summary>The standalone instance we own, or null.</summary>
            public AssetLinker? Instance { get; set; }

            /// <summary>
            /// Whether this track has had its one turn on screen.
            /// </summary>
            /// <remarks>
            /// Kept apart from <see cref="Instance"/> being non-null, which is
            /// the obvious way to write it and is wrong. The pool is flushed on
            /// combat teardown and nothing promises that lands after we stop, so
            /// an instance can be destroyed from under us mid-window — and a
            /// track whose eligibility was "holds no instance" would then be
            /// re-instantiated on <b>every frame</b> for the rest of its window.
            /// One effect, once.
            /// </remarks>
            public bool Revealed { get; set; }

            /// <summary>
            /// Revealed after its own window had already closed.
            /// </summary>
            /// <remarks>
            /// These are exactly the effects the interval activation test
            /// exists to save — a muzzle flash lives under a tenth of a second
            /// and a frame is a thirtieth, so the cursor lands past the end of
            /// one on the very frame it first sees it. Counted separately
            /// because whether they draw anything at all is the open question
            /// the whole test rests on.
            /// </remarks>
            public bool RevealedLate { get; set; }

            /// <summary>Whether its first sample has been measured yet.</summary>
            public bool Measured { get; set; }

            /// <summary>
            /// Set once this track has been given up on, so it is never retried.
            /// </summary>
            /// <remarks>
            /// Not optional. Vanilla re-attempts activation <b>every frame</b>
            /// for an unassigned active track, and an unresolvable key makes
            /// <c>IsInstanceAvailable</c> log a warning on every one of those —
            /// so a single bad key is a warning per frame for its whole window.
            /// One line, then silence.
            /// </remarks>
            public bool Abandoned { get; set; }
        }
    }
}
