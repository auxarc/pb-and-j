using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// THROWAWAY. Everything that decides when a unit was visible during a turn,
    /// on whichever machine you run it.
    /// </summary>
    /// <remarks>
    /// Written because four rounds of reading the decompile produced four
    /// different accounts of the same mechanism, three of which were wrong. The
    /// arguments were about facts a single line of output settles:
    /// <list type="bullet">
    /// <item>Does a revealed unit on a <b>client</b> carry <c>LandingData</c>?
    /// That one fact is the whole safety argument for replicating an arrival
    /// time — every host-clock-versus-client-clock consumer of the value is
    /// gated on it. Two arguments that it must be false have already been
    /// refuted.</item>
    /// <item>Can <c>keyframeReveal</c> and <c>keyframeHidden</c> both be set for
    /// one unit in one window? The playback model assumes at most one transition
    /// per turn, and the recorder holds one slot for each with no obvious
    /// interlock.</item>
    /// <item>Are those stamps inside the current window, or left over from an
    /// earlier turn? <c>experimentalMode</c> never clears them, so the answer
    /// decides whether they can be sent unwindowed.</item>
    /// <item>Which of the two classes a unit is in — no recorder entry at all,
    /// or an entry whose first key is at reveal time. The two demand opposite
    /// handling and the recon file has claimed each of them exclusively.</item>
    /// </list>
    /// <para>
    /// Run it after executing a turn, on both machines, against the same unit,
    /// and diff the lines. It reads only public statics and never writes.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class VisibilityProbeGlue
    {
        public static string VisProbe(string unitName)
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }

            CombatEntity? found = null;
            PersistentEntity? persistent = null;
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var p = IDUtility.GetLinkedPersistentEntity(unit);
                if (p != null && p.hasNameInternal && p.nameInternal.s == unitName)
                {
                    found = unit;
                    persistent = p;
                    break;
                }
            }
            if (found == null || persistent == null)
            {
                return "[pb-and-j] no unit named '" + unitName + "' in this combat";
            }

            var combat = Contexts.sharedInstance.combat;
            var sb = new StringBuilder();
            sb.Append("[pb-and-j] vis-probe '").Append(unitName).Append("' id=").Append(found.id.id);

            // The unit's own state — the inputs a snapshot carries today.
            sb.Append(" | hidden=").Append(found.isHidden);
            sb.Append(" detectable=").Append(found.isHiddenDetectable);
            sb.Append(" deployed=").Append(persistent.isUnitDeployed);
            sb.Append(" destroyed=").Append(found.isDestroyed);

            // THE fact the safety argument rests on. Probe a unit the machine has
            // just seen revealed mid-combat: LandingData is written at activation,
            // so an initial-roster unit reading false proves nothing.
            sb.Append(" | landing=").Append(found.hasLandingData);
            sb.Append(" landingCustom=").Append(found.hasLandingDataCustom);
            sb.Append(" arrival=").Append(Time(found.hasArrivalTime, found.hasArrivalTime ? found.arrivalTime.f : 0f));

            // The window, so every stamp below can be read as inside it or not.
            sb.Append(" | simTime=").Append(Num(combat.hasSimulationTime ? combat.simulationTime.f : -1f));
            sb.Append(" turnStart=").Append(Num(CombatReplayHelper.turnStartTime));
            sb.Append(" previewLimit=").Append(Num(CombatReplayHelper.previewTimeLimit));
            sb.Append(" experimental=").Append(CombatReplayHelper.experimentalMode);

            // The recorder. "No entry at all" and "an entry whose first key is at
            // reveal time" are different units needing different fixes, and this
            // is what tells them apart.
            var tracks = CombatReplayHelper.units;
            if (tracks == null)
            {
                sb.Append(" | recorder=null");
            }
            else if (!tracks.TryGetValue(found.id.id, out var track) || track == null)
            {
                sb.Append(" | entry=NONE recorderUnits=").Append(tracks.Count);
            }
            else
            {
                sb.Append(" | entry=yes recorderUnits=").Append(tracks.Count);
                sb.Append(" reveal=").Append(Keyframe(track.keyframeReveal));
                sb.Append(" hidden=").Append(Keyframe(track.keyframeHidden));
                sb.Append(" visibleLast=").Append(track.visibleLast);
                sb.Append(" recordedLast=").Append(Num(track.timeRecordedLast));

                Span(sb, "xform", track.keyframesTransform?.Count ?? 0,
                    track.keyframesTransform != null && track.keyframesTransform.Count > 0
                        ? track.keyframesTransform[0].time : 0f,
                    track.keyframesTransform != null && track.keyframesTransform.Count > 0
                        ? track.keyframesTransform[track.keyframesTransform.Count - 1].time : 0f);

                Span(sb, "poses", track.keyframesPoses?.Count ?? 0,
                    track.keyframesPoses != null && track.keyframesPoses.Count > 0
                        ? track.keyframesPoses[0].time : 0f,
                    track.keyframesPoses != null && track.keyframesPoses.Count > 0
                        ? track.keyframesPoses[track.keyframesPoses.Count - 1].time : 0f);
            }

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        // First and last stamp, not just a count: whether the first key sits at
        // the window start or partway into it IS the distinction being measured.
        private static void Span(StringBuilder sb, string name, int count, float first, float last)
        {
            sb.Append(' ').Append(name).Append('=').Append(count);
            if (count > 0)
            {
                sb.Append('[').Append(Num(first)).Append("..").Append(Num(last)).Append(']');
            }
        }

        private static string Keyframe(ReplayKeyframe? k)
        {
            return k == null ? "-" : Num(k.time);
        }

        private static string Time(bool has, float value)
        {
            return has ? Num(value) : "-";
        }

        private static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static void RegisterConsoleCommands()
        {
            var method = typeof(VisibilityProbeGlue).GetMethod(
                nameof(VisProbe), BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.vis-probe"));
        }
    }
}
