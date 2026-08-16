using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Resolves each weapon light's world position at the instant it is fired.
    /// </summary>
    /// <remarks>
    /// <b>This patch exists because the value cannot be recovered later, and
    /// that is the whole point of it.</b>
    /// <para>
    /// <c>CombatReplayHelper.OnUnitLightWeapon</c> stores the live
    /// <c>firingTransform</c> on the keyframe and nothing else
    /// (<c>CombatReplayHelper.cs:1951-1962</c>). Downstream,
    /// <c>UnitLightManager.OnWeaponLight</c> uses that transform for exactly one
    /// thing — <c>firingTransform.TransformPoint(new Vector3(0, 0,
    /// positionOffset))</c> at <c>UnitLightManager.cs:281</c> — and never
    /// retains it. So the only thing that has to travel is that one world point.
    /// </para>
    /// <para>
    /// ⚠️ <b>Resolving it when the turn is harvested would be silently wrong.</b>
    /// <c>CombatGameBridge.CaptureKeyframes</c> runs at end of turn, by which
    /// point the barrel has moved: every flash in the turn would be placed where
    /// that weapon finished, not where it fired, and every count and log line
    /// would still read correct. An adversarial review caught this before it was
    /// written; nothing on screen would have caught it after.
    /// </para>
    /// <para>
    /// A null transform is recorded as nothing at all, and that is exact parity
    /// rather than a loss. The live path <c>IgniteWeaponLight</c>
    /// (<c>UnitVisualManager.cs:2221-2227</c>) forwards to the same
    /// <c>OnWeaponLight</c> with the same null early-out, so a shot whose firing
    /// transform was null lit nothing on the host either.
    /// </para>
    /// <para>
    /// Keyed by the combat entity id, matching how the recorder itself keys
    /// units. The cache is a within-turn structure only — it is cleared when the
    /// bridge harvests it, and again on combat teardown.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatReplayHelper), "OnUnitLightWeapon")]
    internal static class WeaponLightPatches
    {
        private static readonly Dictionary<int, List<UnitLightKey>> captured =
            new Dictionary<int, List<UnitLightKey>>();

        /// <summary>Flashes whose firing transform was null when they fired.</summary>
        /// <remarks>
        /// Reported rather than merely skipped. Zero is the expected reading and
        /// a large number means this campaign's weapons carry light blocks
        /// without visual blocks — <c>ScheduledAttackSystem.cs:301-304</c>
        /// returns a null transform <b>silently</b> in that case, with no
        /// warning anywhere, which is why a log grep cannot answer it.
        /// </remarks>
        internal static int SkippedNoTransform { get; private set; }

        [HarmonyPostfix]
        private static void Capture(
            CombatEntity unitCombat,
            string socket,
            Transform firingTransform,
            Color color,
            float intensity,
            float durationBuildup,
            float durationStable,
            float durationFade,
            float positionOffset)
        {
            // Only a recording host has anything to capture, and the recorder
            // itself is gated the same way — a track it refused to open is one
            // we must not invent keys for.
            if (unitCombat == null || !CombatReplayHelper.IsRecordingAllowed())
            {
                return;
            }

            if (firingTransform == null)
            {
                SkippedNoTransform++;
                return;
            }

            var id = unitCombat.id.id;

            // The one line this whole patch exists for. Evaluated here, while
            // the barrel is still where it fired from.
            var point = firingTransform.TransformPoint(new Vector3(0f, 0f, positionOffset));

            if (!captured.TryGetValue(id, out var keys))
            {
                keys = new List<UnitLightKey>();
                captured[id] = keys;
            }

            keys.Add(new UnitLightKey(
                CombatReplayHelper.GetSimulationTime(),
                socket,
                new Vec3(point.x, point.y, point.z),
                new Vec4(color.r, color.g, color.b, color.a),
                intensity,
                durationBuildup,
                durationStable,
                durationFade));
        }

        /// <summary>This unit's flashes so far this turn, or null if it has none.</summary>
        internal static List<UnitLightKey>? For(int combatEntityId)
        {
            return captured.TryGetValue(combatEntityId, out var keys) ? keys : null;
        }

        /// <summary>Every unit with flashes, so the harvest can spot the orphans.</summary>
        /// <remarks>
        /// The bridge needs this and not just <see cref="For"/>, because the
        /// loss worth reporting is a unit that fired and got <i>no pose track</i>
        /// — which by definition is not in the collection the harvest walks.
        /// </remarks>
        internal static IEnumerable<KeyValuePair<int, List<UnitLightKey>>> All()
        {
            return captured;
        }

        internal static void Clear()
        {
            captured.Clear();
            SkippedNoTransform = 0;
        }
    }
}
