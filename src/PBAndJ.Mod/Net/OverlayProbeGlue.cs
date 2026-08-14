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
    /// THROWAWAY (M13). Why a client draws "no data" over a unit the host draws
    /// normally.
    /// </summary>
    /// <remarks>
    /// Written after a theory died. The overlay's <c>widgetUnknown</c> is gated
    /// on <c>predictionTime - simulationTime &gt; predictionTimeHorizon</c>, and
    /// a client's combat clock is frozen — which looked like the answer until
    /// both clocks were measured and <c>predictionTime</c> turned out to be 0.00
    /// on host and client alike, making the difference 0 and -15 respectively.
    /// Neither can exceed a horizon clamped to 0..5.
    /// <para>
    /// So this dumps which widgets are actually active and every component that
    /// gates one, rather than reasoning about which ought to be. Run it on both
    /// machines against the same unit and diff the two lines.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class OverlayProbeGlue
    {
        public static string OverlayProbe(string unitName)
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

            var sb = new StringBuilder();
            sb.Append("[pb-and-j] overlay-probe '").Append(unitName).Append("' id=").Append(found.id.id);
            sb.Append(" | hidden=").Append(found.isHidden);
            sb.Append(" detectable=").Append(found.isHiddenDetectable);
            sb.Append(" deployed=").Append(persistent.isUnitDeployed);
            sb.Append(" wrecked=").Append(persistent.isWrecked);
            sb.Append(" destroyed=").Append(found.isDestroyed);
            sb.Append(" composite=").Append(found.hasUnitCompositeLink);
            sb.Append(" history=").Append(found.hasExecutionHistory);
            sb.Append(" landing=").Append(found.hasLandingData);
            sb.Append(" arrival=").Append(found.hasArrivalTime
                ? found.arrivalTime.f.ToString("0.00", CultureInfo.InvariantCulture)
                : "-");
            sb.Append(" horizon=").Append(found.hasPredictionTimeHorizon
                ? found.predictionTimeHorizon.f.ToString("0.00", CultureInfo.InvariantCulture)
                : "-");
            sb.Append(" replayActive=").Append(CombatReplayHelper.activeLast);

            // ins is private static; overlays on it is public.
            var insField = typeof(CIHelperOverlays).GetField(
                "ins", BindingFlags.Static | BindingFlags.NonPublic);
            var overlaysHost = insField?.GetValue(null) as CIHelperOverlays;
            if (overlaysHost == null || overlaysHost.overlays == null)
            {
                sb.Append(" | NO OVERLAY HELPER");
                Debug.Log(sb.ToString());
                return sb.ToString();
            }
            if (!overlaysHost.overlays.TryGetValue(found.id.id, out var overlay) || overlay == null)
            {
                sb.Append(" | NO OVERLAY for this unit");
                Debug.Log(sb.ToString());
                return sb.ToString();
            }

            sb.Append(" | widgets:");
            Widget(sb, "status", overlay.widgetStatus);
            Widget(sb, "landing", overlay.widgetLanding);
            Widget(sb, "distant", overlay.widgetDistant);
            Widget(sb, "priority", overlay.widgetPriority);
            Widget(sb, "crash", overlay.widgetCrash);
            Widget(sb, "unknown", overlay.widgetUnknown);
            Widget(sb, "friendlyHint", overlay.friendlyHint);
            Holder(sb, "equipment", overlay.holderEquipment);
            Holder(sb, "nonReplayed", overlay.holderNonReplayed);
            Label(sb, "role", overlay.labelUnitRole);
            Label(sb, "dmg", overlay.labelSummaryDamage);
            Label(sb, "other", overlay.labelSummaryOther);
            Label(sb, "landingCd", overlay.labelLandingCountdown);
            Label(sb, "stability", overlay.labelStability);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        private static void Widget(StringBuilder sb, string name, UIWidget? w)
        {
            sb.Append(' ').Append(name).Append('=')
                .Append(w == null ? "null" : (w.gameObject.activeSelf ? "ON" : "off"));
        }

        private static void Holder(StringBuilder sb, string name, GameObject? g)
        {
            sb.Append(' ').Append(name).Append('=')
                .Append(g == null ? "null" : (g.activeSelf ? "ON" : "off"));
        }

        private static void Label(StringBuilder sb, string name, UILabel? l)
        {
            if (l == null)
            {
                sb.Append(' ').Append(name).Append("=null");
                return;
            }
            sb.Append(' ').Append(name).Append('=')
                .Append(l.gameObject.activeSelf ? "ON" : "off")
                .Append('\'').Append(l.text ?? string.Empty).Append('\'');
        }

        internal static void RegisterConsoleCommands()
        {
            var method = typeof(OverlayProbeGlue).GetMethod(
                nameof(OverlayProbe), BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.overlay-probe"));
        }
    }
}
