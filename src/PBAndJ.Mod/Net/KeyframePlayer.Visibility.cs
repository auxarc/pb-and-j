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
    // Revealing and hiding units mid-window.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
        /// <summary>
        /// Notes a unit whose visibility moved during the window, if it did.
        /// </summary>
        /// <remarks>
        /// The stamps come from two places because only one of them can carry
        /// each case. A tracked unit's reveal and hide are the recorder's own,
        /// clipped to the window by the host — exact, and absent for a unit
        /// activated during the planning phase, which is right: the recorder
        /// held that unit from the window's first frame, so the host drew it
        /// throughout.
        /// <para>
        /// A unit with <i>no</i> track has no recorder entry to have stamped
        /// anything, so its reveal comes from the snapshot's <c>ArrivalTime</c>,
        /// read off the ECS. That read is safe only because the snapshot is
        /// applied before playback starts — the host sends it first from a
        /// single site and effects run in order — and it is the only carrier
        /// that case has. There is no matching hide: the game has no hide-time
        /// component, and a unit hidden all window has no track to stamp.
        /// </para>
        /// </remarks>
        private static void Watch(CombatEntity unit, UnitTrack? track)
        {
            var reveal = track != null ? track.RevealTime : ArrivalOf(unit);
            var hide = track != null ? track.HideTime : ReplayVisibility.None;

            // Nothing moved, which is almost every unit of almost every turn.
            if (float.IsNegativeInfinity(reveal) && float.IsNegativeInfinity(hide))
            {
                return;
            }

            var watch = new VisibilityWatch(unit.id.id, unit.isHidden, reveal, hide)
            {
                View = unit.hasCombatView ? unit.combatView.view : null,
                Projected = unit.hasProjectionView ? unit.projectionView.view : null,
                Puppet = unit.hasPuppetView ? unit.puppetView.view : null,
            };
            watches.Add(watch);
        }


        // Only meaningful as a reveal instant when it falls inside the window.
        // A client manufactures an arrival time for every deployed unit on load,
        // so an unfiltered read would treat the whole player squad as pending.
        private static float ArrivalOf(CombatEntity unit)
        {
            if (!unit.hasArrivalTime)
            {
                return ReplayVisibility.None;
            }
            var arrival = unit.arrivalTime.f;
            return arrival > windowStart && arrival <= windowEnd
                ? arrival
                : ReplayVisibility.None;
        }

        /// <summary>
        /// Puts every watched unit where the cursor says the host had it.
        /// </summary>
        /// <remarks>
        /// Runs every frame rather than only on a change, and the reason is a
        /// race rather than laziness. A unit hidden all combat has no world
        /// marker to suppress at install — <c>OnUnitVisibilityChanged</c> is a
        /// filter over the existing marker list and a no-op on an empty one —
        /// and one is then <i>created, visible</i> by
        /// <c>CombatUILinkInWorldMarkers</c>, which collects on Position and
        /// skips only units flagged <c>isHidden</c>. Deferral deliberately never
        /// touches that flag, and the snapshot's own position write is what
        /// fires the collector. So the marker appears after our install call,
        /// floating at the unit's end-of-turn position.
        /// <para>
        /// Re-asserting each frame bounds that leak to a single frame. It does
        /// not eliminate it: whether this pass runs before or after the Entitas
        /// reactive tick within a frame is not pinned down. The call is a
        /// <c>SetActive</c> over a cached list, so the cost of being sure is
        /// negligible and the deferred set is small by construction.
        /// </para>
        /// </remarks>
        private static void ApplyVisibility()
        {
            for (var i = 0; i < watches.Count; i++)
            {
                var watch = watches[i];
                var visible = ReplayVisibility.IsVisibleAt(
                    watch.EndHidden, watch.Hide, watch.Reveal, cursor);
                Show(watch, visible);
            }
        }

        /// <summary>
        /// Applies one unit's visibility, waking it first if it is going away.
        /// </summary>
        /// <remarks>
        /// The wake-before-hide order is load-bearing and is the one sequencing
        /// rule here that no amount of testing on a host would surface.
        /// <c>CombatPuppetView.OnVisibility</c> is a <c>SetActive</c> on the
        /// puppet view's own root, and the puppet master and behaviour objects
        /// that <see cref="Dress(CombatEntity, Target, UnitPoseTrack)"/> deactivates
        /// are children of it. Hide first
        /// and the later unwind sets those children active <i>under an inactive
        /// root</i>, so no <c>OnEnable</c> ever fires, the puppet never
        /// re-initialises, and the cascade goes off later in a state no vanilla
        /// path produces.
        /// <para>
        /// So a unit being hidden is unwound first — while its root is still
        /// active and the wake runs in the orderly direction — and only then
        /// hidden. Its remaining keys are no loss: it is invisible for the rest
        /// of the window by definition.
        /// </para>
        /// </remarks>
        private static void Show(VisibilityWatch watch, bool visible)
        {
            var changed = watch.Applied != visible;
            watch.Applied = visible;

            if (changed && !visible)
            {
                WakeOne(watch);
            }

            if (changed)
            {
                if (watch.View != null)
                {
                    watch.View.OnVisibility(visible);
                }
                if (watch.Projected != null)
                {
                    watch.Projected.OnVisibility(visible);
                }
                if (watch.Puppet != null)
                {
                    watch.Puppet.OnVisibility(visible);
                }
                CIHelperOverlays.OnUnitVisibilityChanged(watch.Id, visible);
            }

            // Unconditional, unlike the rest: this is the half that loses a race
            // to a marker created after our install call. Cheap and idempotent.
            if (!visible || changed)
            {
                PhantomBrigade.Combat.CIHelperWorldMarkers.OnUnitVisibilityChanged(watch.Id, visible);
            }
        }

        /// <summary>
        /// Hands every watched unit back to the snapshot's own answer.
        /// </summary>
        /// <remarks>
        /// Read live off the entity rather than from anything captured at
        /// install. A new turn's keyframes arrive <i>after</i> that turn's
        /// snapshot has already been applied, and <see cref="Play"/> opens by
        /// stopping the previous window — so restoring a remembered value would
        /// overwrite fresher truth with staler. The entity is the truth; we were
        /// only ever borrowing it for the length of a window.
        /// </remarks>
        private static void RestoreVisibility()
        {
            for (var i = 0; i < watches.Count; i++)
            {
                var watch = watches[i];
                var unit = IDUtility.GetCombatEntity(watch.Id);
                var visible = unit == null || !unit.isHidden;
                if (watch.Applied == visible)
                {
                    continue;
                }
                watch.Applied = null;
                if (watch.View != null)
                {
                    watch.View.OnVisibility(visible);
                }
                if (watch.Projected != null)
                {
                    watch.Projected.OnVisibility(visible);
                }
                if (watch.Puppet != null)
                {
                    watch.Puppet.OnVisibility(visible);
                }
                PhantomBrigade.Combat.CIHelperWorldMarkers.OnUnitVisibilityChanged(watch.Id, visible);
                CIHelperOverlays.OnUnitVisibilityChanged(watch.Id, visible);
            }
            watches.Clear();
        }
    }
}
