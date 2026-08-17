using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Pre-flight for M14 stage C: whether reaction glows and melee shockwave
    /// trails are even reachable on this machine, and a way to see one without a
    /// second instance.
    /// </summary>
    /// <remarks>
    /// Built because a stage C playtest has two ways to show nothing and they
    /// look identical from the outside: the feature could be broken, or the
    /// fight could simply have contained no melee and no reaction-lit action.
    /// Stage A learned this the expensive way — <c>AssetTrailsNotCaptured</c>
    /// exists precisely so "we lost it" and "there was nothing to lose" stop
    /// reading the same, and it paid for itself the first time a log was read.
    /// <para>
    /// So this answers, before the rig is started: does the recorder hold
    /// anything, do this machine's units carry the rig the drive needs, and does
    /// the data a recorded key points at resolve.
    /// </para>
    /// <para>
    /// <c>pbj.stagec-inject</c> is the other half, and it is the one that
    /// actually de-risks the run: it drives the client-side path with synthetic
    /// values on a single instance, so the visual can be confirmed with no host,
    /// no wire and no melee. That is the same argument stage B's
    /// <c>pbj.fx-beam-inject</c> made — no mech in this campaign carries a beam,
    /// so the measurement could not otherwise have been run at all.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class StageCProbeGlue
    {
        // What the injector drives along, in metres, ahead of the unit. Long
        // enough to be unmistakable and short enough to stay on the ground the
        // unit is standing on.
        private const float SweepLength = 12f;

        private static CombatEntity? injectedUnit;
        private static float injectStarted;
        private static float injectSeconds;
        private static string injectShockwave = string.Empty;
        private static Vector3 injectStart;
        private static Vector3 injectEnd;
        private static bool injectPartUsed;

        /// <summary>
        /// Reports whether stage C has anything to show, and on whose side.
        /// </summary>
        [Command("pbj.stagec-probe", "M14 stage C: recorder contents, unit rig and data resolution")]
        public static string StageCProbe()
        {
            var sb = new StringBuilder("[pb-and-j] stage C | ");

            ReportRecorder(sb);
            sb.Append(" | ");
            ReportRig(sb);
            sb.Append(" | ");
            ReportData(sb);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <summary>
        /// What the host's recorder is holding right now.
        /// </summary>
        /// <remarks>
        /// Zero here on a client is expected and is not a fault:
        /// <c>CombatReplayHelper.units</c> is only ever populated by
        /// <c>OnExecutionStart</c>, which runs off <c>Simulating</c>. Read this
        /// on the HOST after executing a turn.
        /// </remarks>
        private static void ReportRecorder(StringBuilder sb)
        {
            var units = CombatReplayHelper.units;
            if (units == null || units.Count == 0)
            {
                sb.Append("recorder=empty (expected on a client)");
                return;
            }

            var tracked = 0;
            var pings = 0;
            var swings = 0;
            var unitsWithPings = 0;
            var unitsWithSwings = 0;
            var earliest = float.MaxValue;
            var latest = float.MinValue;

            foreach (var pair in units)
            {
                var track = pair.Value;
                if (track == null)
                {
                    continue;
                }
                tracked++;

                var reactions = track.keyframesLightsReactions;
                if (reactions != null && reactions.Count > 0)
                {
                    pings += reactions.Count;
                    unitsWithPings++;
                    for (var i = 0; i < reactions.Count; i++)
                    {
                        var t = reactions[i].time;
                        if (t < earliest)
                        {
                            earliest = t;
                        }
                        if (t > latest)
                        {
                            latest = t;
                        }
                    }
                }

                var melees = track.entitiesMelee;
                if (melees != null && melees.Count > 0)
                {
                    swings += melees.Count;
                    unitsWithSwings++;
                }
            }

            sb.Append("recorder: units=").Append(tracked)
                .Append(" pings=").Append(pings)
                .Append(" on ").Append(unitsWithPings).Append(" unit(s)")
                .Append(" swings=").Append(swings)
                .Append(" on ").Append(unitsWithSwings).Append(" unit(s)");

            // The window matters as much as the count. These lists accumulate
            // across the whole fight when the recorder retains tracks, so a
            // large ping count spanning many turns is the expected shape — and
            // the capture slice, not the cap, is what keeps it honest.
            if (pings > 0)
            {
                sb.Append(" pingSpan=").Append(Round(earliest)).Append("..").Append(Round(latest));
            }

            sb.Append(" turnStart=").Append(Round(CombatReplayHelper.turnStartTime));
        }

        /// <summary>
        /// Whether this machine's units carry what the drive needs.
        /// </summary>
        /// <remarks>
        /// Three separate questions, deliberately not collapsed into one number:
        /// a null light manager loses the glow AND the counter; a null
        /// <c>reactionHolder</c> means the glow branch cannot fire at all
        /// (<c>UnitLightManager.cs:137</c>) and is a silent nothing rather than a
        /// fault; and a holder present with a null <c>reactionAmbient</c> or
        /// <c>reactionGlow</c> is the NRE shape the drive is wrapped against.
        /// <para>
        /// The melee count is separate again, because the trail needs the
        /// concrete <c>UnitVFXManager</c> — <c>UnitVFXManagerBase</c> is not
        /// enough, and tanks come through <c>UnitVisualManagerSimple</c>.
        /// </para>
        /// </remarks>
        private static void ReportRig(StringBuilder sb)
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                sb.Append("rig: not in combat");
                return;
            }

            var holderField = typeof(UnitLightManager).GetField(
                "reactionTimeLast", BindingFlags.Instance | BindingFlags.NonPublic);

            var units = 0;
            var withManager = 0;
            var withHolder = 0;
            var incomplete = 0;
            var withMeleeVfx = 0;
            var pinged = 0;
            var duration = -1f;

            foreach (var unit in combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                if (!unit.hasCombatView || unit.combatView.view == null)
                {
                    continue;
                }
                units++;

                var visualManager = unit.combatView.view.visualManager;
                if (visualManager == null)
                {
                    continue;
                }

                var manager = visualManager.GetLightManager();
                if (manager != null)
                {
                    withManager++;
                    if (manager.reactionHolder != null)
                    {
                        withHolder++;

                        // Only meaningful where the holder exists: these are the
                        // two the game dereferences without a null check once the
                        // branch arms.
                        if (manager.reactionAmbient == null || manager.reactionGlow == null)
                        {
                            incomplete++;
                        }
                    }

                    if (duration < 0f)
                    {
                        duration = manager.reactionDuration;
                    }

                    var stamp = holderField?.GetValue(manager);
                    if (stamp is float value && value > 0f)
                    {
                        pinged++;
                    }
                }

                if (visualManager.GetVFXManager() is UnitVFXManager)
                {
                    withMeleeVfx++;
                }
            }

            sb.Append("rig: units=").Append(units)
                .Append(" lightMgr=").Append(withManager)
                .Append(" reactionHolder=").Append(withHolder)
                .Append(" holderIncomplete=").Append(incomplete)
                .Append(" meleeVfx=").Append(withMeleeVfx)
                .Append(" alreadyPinged=").Append(pinged);

            if (duration >= 0f)
            {
                // Sub-frame risk in one number. The default is 0.10s, and a
                // playback frame gap can exceed it — so a glow can be driven
                // correctly and still never be seen.
                sb.Append(" reactionDuration=").Append(Round(duration));
            }
        }

        /// <summary>
        /// Whether the data a recorded key points at exists on this machine.
        /// </summary>
        private static void ReportData(StringBuilder sb)
        {
            var shockwaves = DataMultiLinker<DataContainerEquipmentShockwave>.data;
            sb.Append("data: shockwaves=").Append(shockwaves == null ? 0 : shockwaves.Count);

            // Whether the feature is reachable in this campaign at all. The flag
            // defaults TRUE, so a low number here would be the surprise — and it
            // is the difference between "the drive is broken" and "no action in
            // that fight was reaction-lit".
            var actions = DataMultiLinker<DataContainerAction>.data;
            var reactionLit = 0;
            var total = 0;
            if (actions != null)
            {
                foreach (var pair in actions)
                {
                    total++;
                    var visuals = pair.Value != null ? pair.Value.dataVisualsOnStart : null;
                    if (visuals != null && visuals.reactionLightsUsed)
                    {
                        reactionLit++;
                    }
                }
            }

            sb.Append(" reactionLitActions=").Append(reactionLit).Append('/').Append(total);

            var anim = DataShortcuts.anim;
            sb.Append(" curves=")
                .Append(anim == null ? "no anim settings"
                    : (anim.timeRemapMeleeStandard == null ? "standard NULL" : "standard ok")
                        + "," + (anim.timeRemapMeleeFallback == null ? "fallback NULL" : "fallback ok"));
        }

        /// <summary>
        /// Drives a reaction glow and a shockwave trail on one unit, with
        /// synthetic values, for a few seconds.
        /// </summary>
        /// <remarks>
        /// The point is to separate "the drive works" from "the fight had a
        /// melee in it", on ONE instance, before two are started. It calls
        /// exactly what playback calls — <c>OnReactionPing</c> then
        /// <c>OnTimeChange</c>, and <c>CheckOverlapsWithShockwave</c> with
        /// <c>registerHits: false</c> and a null action — so a green result here
        /// is evidence about the real path and not about a parallel one.
        /// <para>
        /// ⚠️ It drives on a <b>synthetic clock</b>, not the simulation clock.
        /// The sim clock is frozen outside execution, and both effects animate
        /// off elapsed time, so driving them with a stopped clock would show a
        /// single frozen frame and prove nothing.
        /// </para>
        /// <para>
        /// Self-limiting: <see cref="Tick"/> clears the trail and stops when the
        /// window expires, so this cannot leave state a player has to run a
        /// second command to escape — which is the line <c>pbj.fx-hold</c>
        /// crossed and why that one does not ship.
        /// </para>
        /// </remarks>
        [Command("pbj.stagec-inject", "M14 stage C: drive a reaction glow and a shockwave on one unit")]
        public static string StageCInject(int unitIndex, float seconds)
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                return "[pb-and-j] not in combat";
            }

            var units = combat.GetGroup(CombatMatcher.UnitTag).GetEntities();
            if (unitIndex < 0 || unitIndex >= units.Length)
            {
                return "[pb-and-j] no unit " + unitIndex + " (there are " + units.Length + ")";
            }

            var unit = units[unitIndex];
            if (!unit.hasCombatView || unit.combatView.view == null)
            {
                return "[pb-and-j] unit " + unitIndex + " has no combat view";
            }

            var origin = unit.hasPosition ? unit.position.v : Vector3.zero;
            var forward = unit.hasRotation ? unit.rotation.q * Vector3.forward : Vector3.forward;

            injectedUnit = unit;
            injectStarted = Time.realtimeSinceStartup;
            injectSeconds = Mathf.Clamp(seconds, 0.5f, 30f);
            injectStart = origin;
            injectEnd = origin + (forward.normalized * SweepLength);
            injectPartUsed = true;
            injectShockwave = FirstShockwaveKey();

            return "[pb-and-j] driving unit " + unitIndex + " for "
                + injectSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + "s with shockwave '" + injectShockwave + "'";
        }

        /// <summary>
        /// Whichever shockwave the data actually defines, rather than a guess.
        /// </summary>
        private static string FirstShockwaveKey()
        {
            var shockwaves = DataMultiLinker<DataContainerEquipmentShockwave>.data;
            if (shockwaves != null)
            {
                foreach (var pair in shockwaves)
                {
                    return pair.Key;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Advances an armed injection. Called from the same pump the rest of
        /// the glue runs on.
        /// </summary>
        internal static void Tick()
        {
            var unit = injectedUnit;
            if (unit == null)
            {
                return;
            }

            var elapsed = Time.realtimeSinceStartup - injectStarted;
            if (elapsed >= injectSeconds || !unit.hasCombatView || unit.combatView.view == null)
            {
                Disarm(unit);
                return;
            }

            var visualManager = unit.combatView.view.visualManager;
            if (visualManager == null)
            {
                Disarm(unit);
                return;
            }

            try
            {
                DriveGlow(visualManager, elapsed);
                DriveTrail(unit, Mathf.Clamp01(elapsed / injectSeconds));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] stage C injection was refused: " + e.Message);
                Disarm(unit);
            }
        }

        /// <summary>
        /// Re-pings on a loop so the glow is visible for the whole window.
        /// </summary>
        /// <remarks>
        /// One ping would animate for <c>reactionDuration</c> — 0.10s by default
        /// — and be over before anyone looked up. Re-arming each cycle is not
        /// what playback does, and it is not pretending to be: playback's job is
        /// parity with the host, this one's job is to be seen.
        /// </remarks>
        private static void DriveGlow(object visualManager, float elapsed)
        {
            var manager = ((IUnitVisualManager)visualManager).GetLightManager();
            if (manager == null)
            {
                return;
            }

            var period = Mathf.Max(0.2f, manager.reactionDuration * 2f);
            var cycles = Mathf.Floor(elapsed / period);
            manager.OnReactionPing(cycles * period);
            manager.OnTimeChange(elapsed);
        }

        private static void DriveTrail(CombatEntity unit, float normalised)
        {
            var shockwave = DataMultiLinker<DataContainerEquipmentShockwave>
                .GetEntry(injectShockwave, printWarning: false);
            var anim = DataShortcuts.anim;
            var curve = injectPartUsed ? anim.timeRemapMeleeStandard : anim.timeRemapMeleeFallback;

            MeleeUtility.CheckOverlapsWithShockwave(
                unit,
                injectStart,
                injectEnd,
                shockwave,
                curve,
                normalised,
                predictionMode: false,
                registerHits: false,
                actionExecuted: null);
        }

        private static void Disarm(CombatEntity unit)
        {
            injectedUnit = null;

            if (!unit.hasCombatView || unit.combatView.view == null)
            {
                return;
            }

            var visualManager = unit.combatView.view.visualManager;
            var vfx = visualManager != null ? visualManager.GetVFXManager() : null;
            if (vfx is UnitVFXManager melee)
            {
                melee.OnMeleeShockwaveClear();
            }
        }

        private static string Round(float value) =>
            value.ToString("0.00", CultureInfo.InvariantCulture);

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(StageCProbe), "pbj.stagec-probe");
            Add(nameof(StageCInject), "pbj.stagec-inject", typeof(int), typeof(float));
        }

        private static void Add(string methodName, string command, params Type[] signature)
        {
            var method = typeof(StageCProbeGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public,
                null, signature, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
