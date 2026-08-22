using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat.Systems;
using PhantomBrigade.Data;
using PhantomBrigade.Game;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Pre-flight for M15: whether this content can show a destroyed unit at
    /// all, and a way to see one without a second instance or a kill.
    /// </summary>
    /// <remarks>
    /// Built because M15's eye test has <b>three</b> ways to show nothing and
    /// they are indistinguishable from a chair: our code never fired, the code
    /// fired and the socket ships <c>destructionShaderEffect</c> false, or the
    /// unit's blueprint names no destruction FX at all. The last two are
    /// content, and no counter we own can tell them from a defect — the same
    /// trap stage C's probe was written for, where it turned out only 5 of 16
    /// units could show the effect being verified.
    /// <para>
    /// ⚠️ <b>This matters more here than it did for stage C.</b> The user's
    /// reported symptom is a tank, and the tank path is exactly where the
    /// dissolve is config-gated (<c>UnitVisualManagerSimple.cs:596</c>) and
    /// where the wreck FX is a blueprint string that can be empty — the game
    /// itself logs "Main destruction event on this unit has not FX name" when it
    /// is. Reading those before the run turns "it did not work" into a number.
    /// </para>
    /// <para>
    /// <c>pbj.destruct-inject</c> is the other half and the one that de-risks
    /// the run: it drives the client-side path directly on a single instance, so
    /// both halves can be confirmed with no host, no wire and nobody dying.
    /// 🔑 <b>It is safe in a way the plan's proposed injector was not</b> — it
    /// calls the visual manager and <b>never sets the <c>Wrecked</c>
    /// component</b>, so it writes no ECS state, nothing serialized, and nothing
    /// that could reach a campaign save. <c>undo</c> puts every unit and socket
    /// it touched back.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class DestructProbeGlue
    {
        /// <summary>Units this injector has wrecked, for the undo.</summary>
        private static readonly List<int> injectedUnits = new List<int>();

        /// <summary>Unit id and socket this injector has dissolved.</summary>
        private static readonly List<KeyValuePair<int, string>> injectedParts =
            new List<KeyValuePair<int, string>>();

        /// <summary>
        /// Reports whether M15 has anything to show, and on which units, plus
        /// M16's frame-integrity presence per unit.
        /// </summary>
        [Command("pbj.destruct-probe", "M15/M16: wreck content per unit, and frame-integrity presence")]
        public static string DestructProbe()
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }

            var sb = new StringBuilder("[pb-and-j] M15 | ");
            ReportEcs(sb);
            sb.Append(" | ");
            ReportContent(sb);
            sb.Append(" | held: units=").Append(KeyframePlayer.HeldWreckedUnits)
                .Append(" parts=").Append(KeyframePlayer.HeldDestructions);
            // M17 stage 1. Read BETWEEN turns, and read twice: frozen must equal
            // the wrecked-unit count after the window that killed them and again
            // a turn later. A figure that climbs turn on turn is the one defect
            // this feature can have while still looking correct on screen.
            sb.Append(" | pose: frozen=").Append(KeyframePlayer.FrozenUnits)
                .Append(" unfrozen=").Append(KeyframePlayer.Unfrozen);
            // M17 stage 2, reading 6b. set=0 against a host wrecked count above
            // zero means the apply path is dead. refused>0 names an exception in
            // the log. ⚠️ A wreck-visual counter moving is NOT evidence this ran:
            // wrecksPlayed, frozen and wreckFlags answer three different
            // questions and all three have to be read.
            sb.Append(" | wreckFlags: set=").Append(KeyframePlayer.WreckFlagsSet)
                .Append(" cleared=").Append(KeyframePlayer.WreckFlagsCleared)
                .Append(" refused=").Append(KeyframePlayer.WreckFlagsRefused);
            // Reading 6a, and the two halves must be read TOGETHER. filtered=0
            // passed=0 means the Filter was never called at all -- the patch did
            // not apply, or nothing was wrecked. filtered=0 passed>0 means the
            // patch applied and the predicate was false. The predicate is printed
            // beside them so the second case cannot be mistaken for the first.
            sb.Append(" | cascade: filtered=").Append(WreckingPatches.CascadeFiltered)
                .Append(" passed=").Append(WreckingPatches.CascadePassed)
                .Append(" suppressing=").Append(WreckingPatches.SuppressCascade);
            sb.Append(" | ");
            ReportFrameIntegrity(sb);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <summary>
        /// What this machine's own ECS says, which is the host's real answer and
        /// a client's all-zero one.
        /// </summary>
        /// <remarks>
        /// <c>wrecked</c> against <c>destroyed</c> is the reading that settles a
        /// claim M15 rests on and which was argued statically rather than
        /// measured: capture drops units on <c>isDestroyed</c>, the Entitas
        /// lifecycle flag, <b>not</b> on <c>isWrecked</c>. If wrecked units are
        /// routinely not destroyed then a wreck keeps its transform track and
        /// lands at the right instant; if the two move together, every wreck
        /// arrives late via the settle path instead, and <c>wrecksPlayed</c>
        /// would read zero for a reason that is nobody's bug.
        /// </remarks>
        private static void ReportEcs(StringBuilder sb)
        {
            var units = 0;
            var wrecked = 0;
            var wreckedAndDestroyed = 0;
            var parts = 0;
            var composites = 0;
            var hidden = 0;

            foreach (var unit in Contexts.sharedInstance.combat
                .GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                units++;
                if (unit.hasUnitCompositeLink)
                {
                    composites++;
                }
                if (unit.isHidden)
                {
                    hidden++;
                }

                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null)
                {
                    continue;
                }
                if (persistent.isWrecked)
                {
                    wrecked++;
                    if (unit.isDestroyed)
                    {
                        wreckedAndDestroyed++;
                    }
                }
                foreach (var part in EquipmentUtility.GetPartsInUnit(persistent))
                {
                    if (part != null && part.isWrecked)
                    {
                        parts++;
                    }
                }
            }

            sb.Append("ecs: units=").Append(units)
                .Append(" wrecked=").Append(wrecked)
                .Append(" wreckedAndDestroyed=").Append(wreckedAndDestroyed)
                .Append(" parts=").Append(parts)
                .Append(" composites=").Append(composites)
                .Append(" hidden=").Append(hidden);
        }

        /// <summary>
        /// Which units hold <c>unitFrameIntegrity</c>, and at what value. M16's
        /// step 2, and the one reading in its run sheet nothing else substitutes
        /// for.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>The two machines take different paths into combat.</b>
        /// <c>CombatScenarioSetupSystem.cs:60-64</c> early-returns while a load
        /// is in progress, so a client never runs it and its state comes from
        /// <c>DataManagerSave.cs:2293-2301</c> instead — which installs this
        /// component <b>unconditionally at 1f</b> where the host's setup strips
        /// it. The two therefore disagree from combat entry, before anything
        /// crosses the wire, and only <c>KeyframePlayer</c>'s explicit
        /// <c>RemoveUnitFrameIntegrity</c> closes it.
        /// <para>
        /// ⚠️ <b>Every other reading in M16's run sheet passes whether or not
        /// that remove fires</b>, because the per-part values it also syncs are
        /// correct either way. Expected: on a client, <b>true</b> for the player
        /// squad before the first snapshot and <b>false</b> after it, matching
        /// the host — so this must be read on both machines and at both moments,
        /// not once at the end.
        /// </para>
        /// <para>
        /// Only units that <i>hold</i> the component are listed, name-sorted, so
        /// the two windows compare as sets: a name present on one machine and
        /// absent on the other is the defect, and the <c>present=</c> count
        /// alone would hide which unit it was. Names are the wire's own key
        /// (<c>persistent.nameInternal.s</c>) and are stable across machines,
        /// unlike unit indices, which are process-local.
        /// </para>
        /// </remarks>
        private static void ReportFrameIntegrity(StringBuilder sb)
        {
            var units = 0;
            var named = new List<KeyValuePair<string, float>>();

            foreach (var unit in Contexts.sharedInstance.combat
                .GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                units++;

                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                if (!persistent.hasUnitFrameIntegrity)
                {
                    continue;
                }

                named.Add(new KeyValuePair<string, float>(
                    persistent.nameInternal.s, persistent.unitFrameIntegrity.f));
            }

            named.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            sb.Append("frameIntegrity: present=").Append(named.Count)
                .Append('/').Append(units);
            for (var i = 0; i < named.Count; i++)
            {
                sb.Append(' ').Append(named[i].Key).Append('=')
                    .Append(named[i].Value.ToString("0.000", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Whether the blueprints on the field carry anything to draw.
        /// </summary>
        /// <remarks>
        /// Three separate questions, because the two halves of M15 fail
        /// independently and on different data:
        /// <list type="bullet">
        /// <item><c>dissolve</c> — sockets whose <c>destructionShaderEffect</c>
        /// is true. §3.2's ramp is <b>consumed only by these</b>
        /// (<c>UnitVisualManagerSimple.cs:596</c>); on any other socket our
        /// drive is accepted, stored, and ignored.</item>
        /// <item><c>burst</c> — sockets with a non-empty
        /// <c>fxOnDestruction</c>. §3.2's explosion iterates exactly that list,
        /// so an empty one is a silent no-op inside the game's own helper.</item>
        /// <item><c>unitFx</c> — units whose <c>fxNameDestruction</c> is set.
        /// §3.1's wreck on the tank path draws this and nothing else, and the
        /// game logs its own complaint when it is empty.</item>
        /// </list>
        /// A zero in any column is <b>content, not a defect</b>, and knowing
        /// which one is zero before the run is the whole point of reading it.
        /// </remarks>
        private static void ReportContent(StringBuilder sb)
        {
            var withManager = 0;
            var sockets = 0;
            var dissolveSockets = 0;
            var burstSockets = 0;
            var unitFx = 0;
            var unitFxMissing = 0;
            var alreadyDestroyed = 0;
            var simple = 0;

            foreach (var unit in Contexts.sharedInstance.combat
                .GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                if (!unit.hasCombatView || unit.combatView.view == null)
                {
                    continue;
                }
                var visuals = unit.combatView.view.visualManager;
                if (visuals == null)
                {
                    continue;
                }
                withManager++;

                var links = visuals.GetSocketLinks();
                if (links != null)
                {
                    foreach (var pair in links)
                    {
                        var link = pair.Value;
                        if (link == null)
                        {
                            continue;
                        }
                        sockets++;
                        if (link.destructionShaderEffect)
                        {
                            dissolveSockets++;
                        }
                        if (link.fxOnDestruction != null && link.fxOnDestruction.Count > 0)
                        {
                            burstSockets++;
                        }
                    }
                }

                // The tank path only. A mech's wreck comes from its VFX manager
                // rather than a named pool asset, so an absent name there is not
                // the same finding and is deliberately not counted as one.
                if (visuals is UnitVisualManagerSimple tank)
                {
                    simple++;
                    if (!string.IsNullOrEmpty(tank.fxNameDestruction)
                        && tank.fxTransformDestruction != null)
                    {
                        unitFx++;
                    }
                    else
                    {
                        unitFxMissing++;
                    }
                }

                if (DestroyedLast(visuals))
                {
                    alreadyDestroyed++;
                }
            }

            sb.Append("content: mgr=").Append(withManager)
                .Append(" simple=").Append(simple)
                .Append(" sockets=").Append(sockets)
                .Append(" dissolve=").Append(dissolveSockets)
                .Append(" burst=").Append(burstSockets)
                .Append(" unitFx=").Append(unitFx)
                .Append("/").Append(unitFx + unitFxMissing)
                .Append(" destroyedLast=").Append(alreadyDestroyed);
        }

        /// <summary>
        /// Lists one unit's sockets, so the injector can be pointed at a real one.
        /// </summary>
        /// <remarks>
        /// Per socket rather than aggregated, because the aggregate above cannot
        /// answer the question an eye test actually asks — <i>which</i> limb
        /// should be watched. A socket reading <c>dissolve=no</c> is one where
        /// nothing will ever be seen however correct the code is.
        /// </remarks>
        [Command("pbj.destruct-sockets", "M15: one unit's sockets and what each can draw")]
        public static string DestructSockets(int unitIndex)
        {
            if (!TryUnit(unitIndex, out var unit, out var visuals, out var failure))
            {
                return failure;
            }

            var persistent = IDUtility.GetLinkedPersistentEntity(unit);
            var name = persistent != null && persistent.hasNameInternal
                ? persistent.nameInternal.s
                : "?";
            var sb = new StringBuilder("[pb-and-j] unit ")
                .Append(unitIndex).Append(" '").Append(name).Append("'");
            sb.Append(visuals is UnitVisualManagerSimple ? " (tank path)" : " (mech path)");
            sb.Append(" wrecked=").Append(persistent != null && persistent.isWrecked);
            sb.Append(" destroyedLast=").Append(DestroyedLast(visuals));

            var links = visuals.GetSocketLinks();
            if (links == null || links.Count == 0)
            {
                sb.Append(" | no socket links");
            }
            else
            {
                foreach (var pair in links)
                {
                    var link = pair.Value;
                    sb.Append(" | ").Append(pair.Key)
                        .Append(" dissolve=").Append(link != null && link.destructionShaderEffect ? "yes" : "NO")
                        .Append(" burst=").Append(
                            link != null && link.fxOnDestruction != null
                                ? link.fxOnDestruction.Count
                                : 0);
                }
            }

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <summary>
        /// Drives M15's visuals on one unit, with no wire and nobody dying.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>Calls the visual manager and never the ECS.</b> The plan
        /// originally proposed an injector that set <c>isWrecked</c>, and warned
        /// against its own suggestion for good reason: on the drive rig that
        /// writes a host-never-set, <b>serialized</b> flag into a real campaign
        /// save with no unwind. This one is the same shape as the feature it
        /// tests — <c>OnUnitDestruction</c> and <c>OnSocketDestructionChange</c>
        /// are visual calls — so the worst it can leave behind is a picture, and
        /// <c>undo</c> takes even that back.
        /// <para>
        /// Modes: <c>wreck</c> plays the unit's own destruction; <c>part</c>
        /// dissolves one socket (integrity first, exactly as the feature does,
        /// because <c>OnSocketDestructionChange</c> re-applies stored integrity
        /// and defaults it to 1); <c>all</c> does both across every socket.
        /// </para>
        /// </remarks>
        [Command("pbj.destruct-inject", "M15: play a wreck or a part dissolve on one unit")]
        public static string DestructInject(string mode, int unitIndex, string socket = "")
        {
            if (!TryUnit(unitIndex, out var unit, out var visuals, out var failure))
            {
                return failure;
            }

            try
            {
                switch (mode)
                {
                    case "wreck":
                        visuals.OnUnitDestruction();
                        Remember(injectedUnits, unit.id.id);
                        return "[pb-and-j] wreck played on unit " + unitIndex
                            + " — pbj.destruct-inject undo 0 to take it back";

                    case "part":
                        if (string.IsNullOrEmpty(socket))
                        {
                            return "[pb-and-j] name a socket (see pbj.destruct-sockets " + unitIndex + ")";
                        }
                        Dissolve(visuals, unit.id.id, socket);
                        return "[pb-and-j] socket '" + socket + "' dissolved on unit " + unitIndex;

                    case "all":
                    {
                        var links = visuals.GetSocketLinks();
                        var driven = 0;
                        if (links != null)
                        {
                            foreach (var pair in links)
                            {
                                Dissolve(visuals, unit.id.id, pair.Key);
                                driven++;
                            }
                        }
                        visuals.OnUnitDestruction();
                        Remember(injectedUnits, unit.id.id);
                        return "[pb-and-j] wreck plus " + driven + " socket(s) on unit " + unitIndex;
                    }

                    case "undo":
                        return Undo();

                    default:
                        return "[pb-and-j] mode must be wreck, part, all or undo";
                }
            }
            catch (Exception e)
            {
                return "[pb-and-j] refused: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// Puts back everything this injector touched.
        /// </summary>
        /// <remarks>
        /// Reachable at all only because the game provides the inverse of both
        /// calls — <c>OnUnitRevival</c> clears the same <c>destroyedLast</c> flag
        /// <c>OnUnitDestruction</c> sets, and a socket driven to progress 0 over
        /// integrity 1 is a pristine part again. An injector without an undo
        /// would leave the rig's next measurement reading a corpse it created.
        /// </remarks>
        private static string Undo()
        {
            var units = 0;
            var parts = 0;

            for (var i = 0; i < injectedParts.Count; i++)
            {
                var visuals = VisualsOf(injectedParts[i].Key);
                if (visuals == null)
                {
                    continue;
                }
                try
                {
                    visuals.OnIntegrityChange(injectedParts[i].Value, 1f);
                    visuals.OnSocketDestructionChange(injectedParts[i].Value, 0f);
                    parts++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[pb-and-j] undo of a socket was refused: " + e.Message);
                }
            }

            for (var i = 0; i < injectedUnits.Count; i++)
            {
                var visuals = VisualsOf(injectedUnits[i]);
                if (visuals == null)
                {
                    continue;
                }
                try
                {
                    visuals.OnUnitRevival();
                    units++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[pb-and-j] undo of a wreck was refused: " + e.Message);
                }
            }

            injectedParts.Clear();
            injectedUnits.Clear();
            return "[pb-and-j] undone: " + units + " wreck(s), " + parts + " socket(s)";
        }

        // Integrity BEFORE progress, and it is not a stylistic choice:
        // OnSocketDestructionChange ends by re-applying the socket's stored
        // integrity, defaulting to 1f, so the other order paints a dissolve over
        // a part that still reads pristine.
        private static void Dissolve(IUnitVisualManager visuals, int unitId, string socket)
        {
            visuals.OnIntegrityChange(socket, 0f);
            UnitVisualUtility.OnSocketDestruction(visuals, socket, audioUsed: true);
            visuals.OnSocketDestructionChange(socket, 1f);
            injectedParts.Add(new KeyValuePair<int, string>(unitId, socket));
        }

        private static void Remember(List<int> into, int id)
        {
            if (!into.Contains(id))
            {
                into.Add(id);
            }
        }

        private static IUnitVisualManager? VisualsOf(int unitId)
        {
            var unit = IDUtility.GetCombatEntity(unitId);
            if (unit == null || !unit.hasCombatView || unit.combatView.view == null)
            {
                return null;
            }
            return unit.combatView.view.visualManager;
        }

        private static bool TryUnit(
            int unitIndex,
            out CombatEntity unit,
            out IUnitVisualManager visuals,
            out string failure)
        {
            unit = null!;
            visuals = null!;

            if (!IDUtility.IsGameState("combat"))
            {
                failure = "[pb-and-j] not in combat";
                return false;
            }

            var units = Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities();
            if (unitIndex < 0 || unitIndex >= units.Length)
            {
                failure = "[pb-and-j] no unit " + unitIndex + " (there are " + units.Length + ")";
                return false;
            }

            unit = units[unitIndex];
            if (!unit.hasCombatView || unit.combatView.view == null
                || unit.combatView.view.visualManager == null)
            {
                failure = "[pb-and-j] unit " + unitIndex + " has no visual manager";
                return false;
            }

            visuals = unit.combatView.view.visualManager;
            failure = string.Empty;
            return true;
        }

        /// <summary>
        /// Whether this manager already believes its unit is destroyed.
        /// </summary>
        /// <remarks>
        /// Private on both implementations, so read by reflection or not at all
        /// — the same choice <c>pbj.drive-state</c> made for
        /// <c>reactionTimeLast</c>, and for the same reason: a number that might
        /// be a lie is worse than no number.
        /// <para>
        /// It is the reading that separates our two indistinguishable failures.
        /// <c>OnUnitDestruction</c> self-guards on this flag, so a true here
        /// after a run means <b>our call landed and the game chose not to redraw
        /// </b>, where a false means the call never happened at all.
        /// </para>
        /// </remarks>
        private static bool DestroyedLast(IUnitVisualManager visuals)
        {
            var field = visuals.GetType().GetField(
                "destroyedLast", BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(visuals) is bool flag && flag;
        }

        /// <summary>
        /// Whether M17 stage 2's three Harmony patches resolved to real members.
        /// </summary>
        /// <remarks>
        /// 🔴 <b>This exists because nothing else in the build can tell you.</b>
        /// <c>src/PBAndJ.Mod</c> is in <c>UNCOVERED_PROJECTS</c>, so a
        /// <c>[HarmonyPatch]</c> whose target moved, or whose attribute was
        /// dropped by an editing accident, compiles green, deploys green, runs,
        /// and simply never fires. No test and no oracle sees it.
        /// <para>
        /// Two different facts, printed separately because they fail separately.
        /// <c>resolved</c> asks the same question Harmony's own attribute
        /// resolution asks — <c>AccessTools.DeclaredMethod</c> on the declaring
        /// type by name — so a <c>false</c> there means the string form is wrong
        /// or the game moved the member. <c>owners</c> is how many Harmony ids
        /// have patched that method; <c>0</c> against <c>resolved=True</c> means
        /// the target is fine and <b>our patch class never applied</b>.
        /// </para>
        /// <para>
        /// ⚠️ A zero here is never "nothing was wrecked". It is a fact about the
        /// patch set and is readable the moment the game is up, with no fight
        /// loaded and no second instance.
        /// </para>
        /// </remarks>
        [Command("pbj.wreck-patches", "M17 stage 2: did the three wrecking patches resolve and apply?")]
        public static string WreckPatches()
        {
            var sb = new StringBuilder("[pb-and-j] M17 stage 2 patches | ");
            Report(sb, "CombatUnitWreckingSystem.Filter",
                typeof(CombatUnitWreckingSystem), "Filter");
            sb.Append(" | ");
            Report(sb, "CombatUnitDestructionEffectSystem.Filter",
                typeof(CombatUnitDestructionEffectSystem), "Filter");
            sb.Append(" | ");
            Report(sb, "ScenarioUtility.EndCombatWithOutcome",
                typeof(ScenarioUtility), nameof(ScenarioUtility.EndCombatWithOutcome));
            sb.Append(" | suppressCascade=").Append(WreckingPatches.SuppressCascade)
                .Append(" suppressCombatEnd=").Append(WreckingPatches.SuppressCombatEnd);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        private static void Report(StringBuilder sb, string label, Type type, string member)
        {
            // DeclaredMethod, not Method: Harmony's own attribute resolution does
            // not search base types, so a probe that did would report a resolved
            // target for an attribute that cannot find one.
            var method = AccessTools.DeclaredMethod(type, member);
            sb.Append(label).Append(": resolved=").Append(method != null);
            if (method == null)
            {
                sb.Append(" owners=n/a");
                return;
            }
            var info = Harmony.GetPatchInfo(method);
            var owners = info?.Owners;
            sb.Append(" owners=").Append(owners == null ? 0 : owners.Count);
        }

        /// <summary>
        /// The rig's escape hatch out of a fight the client can no longer end
        /// for itself. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Stage 2's <c>EndCombatWithOutcome</c> prefix makes vanilla's
        /// <c>cm.force-victory</c> and <c>cm.force-defeat</c> silent no-ops on a
        /// client — deliberately, because the routes it is really closing are
        /// content-driven and accidental. This restores the <i>deliberate</i>
        /// one.
        /// <para>
        /// ⚠️ There is no <c>cm.end-combat-*</c> command in this game, whatever a
        /// runbook says. The real pair is <c>cm.force-victory</c> /
        /// <c>cm.force-defeat</c>, and both are untouched on the HOST — the
        /// predicate requires a live client session — so the normal rig exit is
        /// still host victory.
        /// </para>
        /// <para>
        /// 🔑 <b>Every branch names itself.</b> A silent return would be
        /// indistinguishable from a bypass that worked, which is exactly the
        /// reading this command exists to make unambiguous.
        /// </para>
        /// </remarks>
        [Command("pbj.force-end", "M17 stage 2: end this client's combat past the outcome prefix")]
        public static string ForceEnd(string outcome)
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] force-end: NOT IN COMBAT — nothing was called";
            }

            CombatOutcome resolved;
            if (string.Equals(outcome, "victory", StringComparison.OrdinalIgnoreCase))
            {
                resolved = CombatOutcome.Victory;
            }
            else if (string.Equals(outcome, "defeat", StringComparison.OrdinalIgnoreCase))
            {
                resolved = CombatOutcome.Defeat;
            }
            else
            {
                return "[pb-and-j] force-end: BAD ARGUMENT '" + outcome
                    + "' — say victory or defeat; nothing was called";
            }

            var armed = WreckingPatches.SuppressCombatEnd;
            try
            {
                WreckingPatches.BypassCombatEndOnce = true;
                ScenarioUtility.EndCombatWithOutcome(resolved, early: true);
            }
            catch (Exception e)
            {
                return "[pb-and-j] force-end: THREW inside the game — "
                    + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                // In a finally rather than after the call: leaving this set would
                // disarm the prefix for the rest of the session, which is the one
                // failure this command could cause that nothing would report.
                WreckingPatches.BypassCombatEndOnce = false;
            }

            return armed
                ? "[pb-and-j] force-end: BYPASSED the prefix and ended combat in " + resolved
                : "[pb-and-j] force-end: prefix was NOT ARMED (no live client session) — "
                    + "the call went straight through and cm.force-* would have worked too; "
                    + "combat ended in " + resolved;
        }

        /// <summary>
        /// Hands these commands to Quantum Console.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>The <c>[Command]</c> attribute alone does nothing here.</b> QC's
        /// own attribute scan does not reach this assembly, so every <c>pbj.*</c>
        /// command in the mod is registered explicitly through
        /// <c>TryAddCommand</c> and the attribute is documentation. Omitting this
        /// call fails in the most expensive possible way: the build is green, the
        /// deploy is green, the game runs, and the command simply does not exist
        /// — which is only discovered with two instances already up and a fight
        /// already loaded.
        /// </remarks>
        internal static void RegisterConsoleCommands()
        {
            Add(nameof(DestructProbe), "pbj.destruct-probe");
            Add(nameof(DestructSockets), "pbj.destruct-sockets", typeof(int));
            Add(nameof(DestructInject), "pbj.destruct-inject",
                typeof(string), typeof(int), typeof(string));
            Add(nameof(WreckPatches), "pbj.wreck-patches");
            Add(nameof(ForceEnd), "pbj.force-end", typeof(string));
        }

        private static void Add(string methodName, string command, params Type[] signature)
        {
            var method = typeof(DestructProbeGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public,
                null, signature, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
