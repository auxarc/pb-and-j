using System;
using System.Collections.Generic;
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
    /// The host debriefing, as three fingerprints that can be compared: what the
    /// salvage screen is showing, what the scenario pre-rolled, and what the
    /// inventory actually holds.
    /// </summary>
    /// <remarks>
    /// Built for reading 9 of the comprehensive rig run (host victory → host
    /// debriefing, before and after the commit) and for the half of design
    /// question 7 that a single machine can answer.
    /// <para>
    /// ⚠️ <b>This is not a second copy of <c>pbj.mg-probe</c>.</b> That probe
    /// already prints every raw field on this screen — budget, cost, stage, and
    /// a per-item dump of the salvage grid — and it stays the right tool for
    /// looking at one screen once. What it cannot do is be <i>compared</i>: its
    /// output is a wall of prose that differs in ordering and in ids between two
    /// machines and between two moments on one machine. This prints the same
    /// state as <b>numbers that are equal or are not</b>, which is the only form
    /// a before/after or host/client comparison can be taken in.
    /// </para>
    /// <para>
    /// 🔑 <b>Two different groupings share the word "group" and they are not the
    /// same set.</b> The run sheet asks for "group count and a per-group
    /// savedOutput fingerprint" as if one list had both, and no such list
    /// exists:
    /// <list type="bullet">
    /// <item><c>salvageGroups</c> is the <b>screen's</b> grouping — one
    /// <c>SalvageGroupData</c> per unit whose wreck is being picked over, keyed
    /// by <c>unitPersistentID</c>, and it carries <b>no</b> savedOutput.</item>
    /// <item><c>rewardGroupsCollapsed</c> is the <b>scenario's</b> grouping —
    /// one <c>CombatRewardGroupCollapsed</c> per reward key on the
    /// <c>CombatDescription</c>, and <c>savedOutput</c> hangs off that
    /// (<c>CombatRewardGroupCollapsed.cs:12</c>).</item>
    /// </list>
    /// Both counts are printed, separately labelled. A reading that quoted one
    /// as the other would compare two machines on the wrong axis and agree by
    /// accident.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>savedOutput == null</c> is normal and is a finding, not a
    /// fault.</b> The game consumes it only when it is present and the group is
    /// a <c>CombatVictory</c> group, and otherwise regenerates the rewards
    /// locally (<c>EquipmentUtility.cs:1151-1156</c>) — which is exactly the
    /// divergence M12d has to care about. So the count of groups
    /// <i>with</i> savedOutput is printed beside the count of groups.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class DebriefProbeGlue
    {
        private const string Tag = "[pb-and-j] debrief-probe";

        /// <summary>
        /// The instrument's own canary input and the answer it must give.
        /// </summary>
        /// <remarks>
        /// The vacuity guard goes on the <b>instrument</b>, never on the input.
        /// A fingerprint function that silently became a no-op — or that was
        /// swapped for a different one during a refactor — would keep printing
        /// eight plausible hex digits, and every "the two agree" and "the two
        /// differ" reading taken with it would be worthless in the same way.
        /// So every report hashes this fixed string and prints the answer beside
        /// the expected one. <c>fpCanary</c> disagreeing with <c>expect</c> means
        /// stop reading the rest of the line.
        /// </remarks>
        private const string CanaryInput = "pb-and-j/fp/v1";

        /// <summary><see cref="CanaryInput"/>'s fingerprint, computed offline.</summary>
        private const string CanaryExpected = "32D6068F";

        /// <summary>
        /// What <see cref="Fingerprint"/> returns for the empty string, which is
        /// the value this probe must never print as if it were data.
        /// </summary>
        /// <remarks>
        /// FNV-1a's offset basis comes back unchanged when nothing is fed to it,
        /// so a fingerprint over an empty collection is <c>811C9DC5</c> — eight
        /// hex digits that look exactly like a measurement and are the signature
        /// of having measured nothing. Two machines that both collected nothing
        /// would "agree" on it. Every fingerprint here is therefore printed as
        /// <c>none (0 items)</c> when the count is zero, and the count is printed
        /// beside the hash when it is not.
        /// </remarks>
        private const string EmptyFingerprint = "811C9DC5";

        /// <summary>
        /// Everything the host's debriefing knows, as a comparable line.
        /// </summary>
        /// <remarks>
        /// ⭐ <b>What a zero means, and it is three different things.</b> The
        /// canary fields are printed <i>first</i> and always, because the trap
        /// this probe exists inside is the one the management probe already fell
        /// into once — reporting a feature absent because the wrong object was
        /// being watched:
        /// <list type="bullet">
        /// <item><c>present=False</c> — <c>CIViewOverworldDebriefing.ins</c> is
        /// null. The view is scene-resident and its <c>Awake</c> sets
        /// <c>ins</c>, with no <c>OnDestroy</c> to clear it, so this is false
        /// only <b>before the overworld scene has ever loaded</b>. Nothing else
        /// on the line means anything: the probe ran too early, in the wrong
        /// scene, or against a build where the singleton moved.</item>
        /// <item><c>present=True entered=False stage=Summary</c> and every count
        /// zero — the debriefing has <b>not been opened this session</b>. This
        /// is the ordinary reading on an idle overworld and is not a
        /// failure.</item>
        /// <item><c>present=True entered=False</c> with <c>stage=Salvage</c> —
        /// ⚠️ <b>the commit has already happened.</b> <c>SalvageFinish</c> calls
        /// <c>TryExit</c> as its second act (<c>:2772-2774</c>), so
        /// <c>entered</c> is <b>false by the time the commit returns</b>. The
        /// run sheet's "entered=False = wrong moment" is true before the commit
        /// and wrong after it, which is why the stage is on the same line.
        /// <b>The post-commit reading is the inventory fingerprint, not the view
        /// — the view has already let go.</b></item>
        /// <item><c>groups=0</c> with <c>entered=True</c> — the screen is open
        /// and the collector found nothing. That is a real zero and the only one
        /// of the four that is.</item>
        /// </list>
        /// </remarks>
        [Command("pbj.debrief-probe", "M12d/q7: debriefing state and salvage fingerprints, host-side")]
        public static string DebriefProbe()
        {
            var report = new StringBuilder();
            var headline = new StringBuilder();

            var view = CIViewOverworldDebriefing.ins;

            headline.Append(Tag)
                .Append(" | fpCanary=").Append(Fingerprint(CanaryInput))
                .Append('/').Append(Fingerprint(string.Empty))
                .Append(" (expect ").Append(CanaryExpected)
                .Append('/').Append(EmptyFingerprint).Append(')')
                .Append(" | present=").Append(view != null);

            if (view == null)
            {
                headline.Append(" | CIViewOverworldDebriefing.ins is NULL — the overworld scene has")
                    .Append(" never loaded in this process, or the singleton moved. Nothing below")
                    .Append(" this point was read.");
                var missing = headline.ToString();
                Debug.Log(missing);
                return missing;
            }

            headline.Append(" entered=").Append(view.IsEntered())
                .Append(" salvageOpen=").Append(view.IsSalvageOpen())
                .Append(" active=").Append(view.gameObject != null && view.gameObject.activeInHierarchy)
                .Append(" stage=").Append(ReadPrivate(view, "stageLast"));

            report.Append(headline).Append('\n');

            Section(report, "budget", sb => AppendBudget(sb, view));
            Section(report, "screen groups (salvageGroups)", sb => AppendScreenGroups(sb, view, headline));
            Section(report, "scenario groups (rewardGroupsCollapsed)", sb => AppendSavedOutput(sb, view, headline));
            Section(report, "inventory (the mg-serials shape)", sb => AppendInventory(sb, headline));

            Debug.Log(report.ToString());

            // The full report goes to Player.log; Quantum Console renders the
            // return value in its own view and drive.sh prints it, so the
            // headline has to carry the numbers a reader compares. The rest is
            // detail nobody diffs by eye.
            return headline.ToString();
        }

        /// <remarks>One section failing must not cost the others.</remarks>
        private static void Section(StringBuilder report, string name, Action<StringBuilder> body)
        {
            report.Append("--- ").Append(name).Append(" ---\n");
            try
            {
                body(report);
            }
            catch (Exception e)
            {
                report.Append("  FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }
        }

        /// <remarks>
        /// The screen's own arithmetic, read rather than recomputed.
        /// <c>salvageBudgetLast</c> is assembled from a point-preset base, a
        /// difficulty multiplier, per-faction offsets, base stats and per-unit
        /// bonuses (<c>CIViewOverworldDebriefing.cs:2123-2204</c>); no mod-side
        /// re-derivation of it would be the number the screen used, and the
        /// number the screen used is the one that reaches the commit at
        /// <c>:2825</c>.
        /// </remarks>
        private static void AppendBudget(StringBuilder report, CIViewOverworldDebriefing view)
        {
            report.Append("  salvageBudgetLast=").Append(ReadPrivate(view, "salvageBudgetLast"))
                .Append(" salvageCostTotal=").Append(ReadPrivate(view, "salvageCostTotal"))
                .Append(" salvageCostValid=").Append(ReadPrivate(view, "salvageCostValid"))
                .Append(" outcomeVictoryLast=").Append(ReadPrivate(view, "outcomeVictoryLast"))
                .Append("\n  counts: skipped=").Append(ReadPrivate(view, "salvageCountSkipped"))
                .Append(" recovered=").Append(ReadPrivate(view, "salvageCountRecovered"))
                .Append(" dismantled=").Append(ReadPrivate(view, "salvageCountDismantled"))
                .Append('\n');
        }

        /// <remarks>
        /// Typed against the game's own public nested <c>SalvageGroupData</c>
        /// rather than walked by reflection field-by-field, so a renamed member
        /// is a build error here instead of a silent <c>&lt;no such field&gt;</c>
        /// in a rig log at midnight. Only the private list holding them needs
        /// reflection.
        /// <para>
        /// The list is constructed at declaration (<c>:427</c>) and so is never
        /// null once the view exists — which means <c>null</c> back from the
        /// read can only be a <b>missing or retyped field</b>, never "no
        /// debriefing yet". Those two are printed as different sentences
        /// because they call for opposite responses.
        /// </para>
        /// </remarks>
        private static void AppendScreenGroups(
            StringBuilder report, CIViewOverworldDebriefing view, StringBuilder headline)
        {
            var raw = ReadPrivateValue(view, "salvageGroups");
            if (raw == null)
            {
                report.Append("  salvageGroups: <no such field, or it is null> — this build's view does")
                    .Append(" not match the one this probe was written against. NOT the same as an")
                    .Append(" empty screen: the field is initialised at its declaration.\n");
                headline.Append(" | screenGroups=UNREADABLE");
                return;
            }

            if (!(raw is List<CIViewOverworldDebriefing.SalvageGroupData> groups))
            {
                report.Append("  salvageGroups: field found but it is a ").Append(raw.GetType().Name)
                    .Append(", not List<SalvageGroupData> — the type changed under this probe.\n");
                headline.Append(" | screenGroups=WRONGTYPE");
                return;
            }

            var items = 0;
            var keys = new List<string>();

            foreach (var group in groups)
            {
                if (group == null)
                {
                    continue;
                }

                var groupItems = group.entities == null ? 0 : group.entities.Count;
                items += groupItems;

                report.Append("  unit ").Append(group.unitPersistentID)
                    .Append(" | costMultiplier ")
                    .Append(group.costMultiplier.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" | entities ").Append(groupItems)
                    .Append(" | parts ").Append(group.parts == null ? 0 : group.parts.Count)
                    .Append(" | subsystems ").Append(group.subsystems == null ? 0 : group.subsystems.Count)
                    .Append('\n');

                if (group.entities == null)
                {
                    continue;
                }

                // Sorted below, so the fingerprint does not depend on the order
                // the screen happened to build its grid in — two machines that
                // hold the same salvage must agree even if they laid it out
                // differently.
                foreach (var entity in group.entities)
                {
                    if (entity == null)
                    {
                        continue;
                    }

                    keys.Add(string.Concat(
                        group.unitPersistentID.ToString(CultureInfo.InvariantCulture),
                        "/",
                        entity.isPart ? "p" : entity.isSubsystem ? "s" : "o",
                        entity.hasSerial ? entity.serial.i.ToString(CultureInfo.InvariantCulture) : "-",
                        entity.hasSalvageSelection
                            ? entity.salvageSelection.dismantle ? "=scrap" : "=recover"
                            : "=skip"));
                }
            }

            var fp = FingerprintOf(keys);
            report.Append("  screenGroups=").Append(groups.Count)
                .Append(" items=").Append(items).Append(" fp=").Append(fp).Append('\n');
            headline.Append(" | screenGroups=").Append(groups.Count)
                .Append(" items=").Append(items).Append(" fp=").Append(fp);
        }

        /// <remarks>
        /// Reads the view's <b>own</b> cached <c>CombatDescription</c>
        /// (<c>cdLast</c>, <c>:265</c>, set by <c>EnterAfterCombat</c> at
        /// <c>:697</c>) rather than re-deriving one from the combat site. The
        /// two are normally the same object and when they are not, the one the
        /// screen used is the one that decides what is handed over — and
        /// re-deriving would quietly answer a different question.
        /// <para>
        /// The fingerprint covers exactly the fields the game replays from:
        /// <c>preset</c>, <c>level</c> and <c>rating</c> per saved part
        /// (<c>CombatRewardSavedPart.cs:5-9</c>), <c>blueprint</c> per saved
        /// subsystem (<c>CombatRewardSavedSubsystem.cs:5</c>), and the resource
        /// pairs. Nothing else in the group survives the round trip, so nothing
        /// else belongs in a digest of it.
        /// </para>
        /// </remarks>
        private static void AppendSavedOutput(
            StringBuilder report, CIViewOverworldDebriefing view, StringBuilder headline)
        {
            if (!(ReadPrivateValue(view, "cdLast") is CombatDescription cd))
            {
                report.Append("  cdLast: <null or not a CombatDescription> — the view has not been")
                    .Append(" entered after a combat in this process. Expected on an idle overworld;")
                    .Append(" a problem only if entered=True above.\n");
                headline.Append(" | cdLast=none");
                return;
            }

            report.Append("  cdLast: scenarioKey=").Append(cd.scenarioKey ?? "<null>")
                .Append(" seed=").Append(cd.scenarioSeed)
                .Append(" areaKey=").Append(cd.areaKey ?? "<null>")
                .Append('\n');

            if (cd.rewardGroupsCollapsed == null)
            {
                report.Append("  rewardGroupsCollapsed is null — this CD carries no reward groups at")
                    .Append(" all, which is a different thing from carrying groups with no saved")
                    .Append(" output.\n");
                headline.Append(" | rewardGroups=none");
                return;
            }

            var withSaved = 0;
            var parts = 0;
            var subsystems = 0;
            var resources = 0;
            var keys = new List<string>();

            foreach (var pair in cd.rewardGroupsCollapsed)
            {
                var group = pair.Value;
                if (group == null)
                {
                    continue;
                }

                var saved = group.savedOutput;
                report.Append("  group '").Append(pair.Key)
                    .Append("' type=").Append(group.type)
                    .Append(" rewards=").Append(group.rewards == null ? 0 : group.rewards.Count)
                    .Append(" savedOutput=").Append(saved == null ? "NONE (will regenerate locally)" : "present")
                    .Append('\n');

                if (saved == null)
                {
                    continue;
                }

                withSaved++;

                if (saved.resources != null)
                {
                    foreach (var resource in saved.resources)
                    {
                        resources++;
                        keys.Add(string.Concat(pair.Key, "/r/", resource.Key, "=",
                            resource.Value.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                if (saved.parts != null)
                {
                    foreach (var part in saved.parts)
                    {
                        if (part == null)
                        {
                            continue;
                        }
                        parts++;
                        keys.Add(string.Concat(pair.Key, "/p/", part.preset ?? "<null>", "@",
                            part.level.ToString(CultureInfo.InvariantCulture), ":",
                            part.rating.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                if (saved.subsystems == null)
                {
                    continue;
                }

                foreach (var subsystem in saved.subsystems)
                {
                    if (subsystem == null)
                    {
                        continue;
                    }
                    subsystems++;
                    keys.Add(string.Concat(pair.Key, "/s/", subsystem.blueprint ?? "<null>"));
                }
            }

            var fp = FingerprintOf(keys);
            report.Append("  rewardGroups=").Append(cd.rewardGroupsCollapsed.Count)
                .Append(" withSavedOutput=").Append(withSaved)
                .Append(" savedParts=").Append(parts)
                .Append(" savedSubsystems=").Append(subsystems)
                .Append(" savedResources=").Append(resources)
                .Append(" fp=").Append(fp).Append('\n');
            headline.Append(" | rewardGroups=").Append(cd.rewardGroupsCollapsed.Count)
                .Append(" withSavedOutput=").Append(withSaved).Append(" savedFp=").Append(fp);
        }

        /// <remarks>
        /// The after-the-commit half, and the reason it is here rather than in
        /// the view section: by the time <c>SalvageFinish</c> returns, the view
        /// has exited and its salvage lists no longer describe anything. What
        /// changed is the <b>inventory</b>, so that is what the second reading
        /// has to be taken from.
        /// <para>
        /// Deliberately the same walk <c>pbj.mg-serials</c> makes — every
        /// equipment entity with a serial, split by <c>isPart</c> /
        /// <c>isSubsystem</c> — so the two commands' counts are directly
        /// comparable and a disagreement between them is a real signal rather
        /// than a difference of definition.
        /// </para>
        /// </remarks>
        private static void AppendInventory(StringBuilder report, StringBuilder headline)
        {
            var parts = 0;
            var subsystems = 0;
            var keys = new List<string>();

            foreach (var entity in Contexts.sharedInstance.equipment.GetEntities())
            {
                if (entity == null || !entity.hasSerial)
                {
                    continue;
                }

                if (entity.isPart)
                {
                    parts++;
                    keys.Add("p" + entity.serial.i.ToString(CultureInfo.InvariantCulture));
                }
                else if (entity.isSubsystem)
                {
                    subsystems++;
                    keys.Add("s" + entity.serial.i.ToString(CultureInfo.InvariantCulture));
                }
            }

            var fp = FingerprintOf(keys);
            report.Append("  parts=").Append(parts).Append(" subsystems=").Append(subsystems)
                .Append(" fp=").Append(fp).Append('\n');
            headline.Append(" | invParts=").Append(parts)
                .Append(" invSubsystems=").Append(subsystems).Append(" invFp=").Append(fp);
        }

        /// <summary>
        /// A sorted collection's fingerprint, or a sentence saying it hashed
        /// nothing.
        /// </summary>
        /// <remarks>
        /// ⭐ The count travels with the hash and an empty collection never
        /// produces one. <c>fp=811C9DC5</c> is what this would print for zero
        /// items if it were allowed to, and two machines that both collected
        /// nothing would read as agreeing — the exact shape of a check whose
        /// "all clear" output is also its "I compared nothing" output.
        /// </remarks>
        private static string FingerprintOf(List<string> keys)
        {
            if (keys.Count == 0)
            {
                return "none (0 items)";
            }

            keys.Sort(StringComparer.Ordinal);

            var joined = new StringBuilder();
            for (var i = 0; i < keys.Count; i++)
            {
                joined.Append(keys[i]).Append('\n');
            }

            return Fingerprint(joined.ToString())
                + " (" + keys.Count.ToString(CultureInfo.InvariantCulture) + " items)";
        }

        /// <summary>FNV-1a, 32-bit, over UTF-16 code units low byte first.</summary>
        /// <remarks>
        /// Written out rather than taken from <c>string.GetHashCode</c>, which
        /// is randomised per process on modern runtimes and would have made
        /// every cross-machine and cross-restart comparison meaningless while
        /// looking exactly like this one. Proven every run against
        /// <see cref="CanaryExpected"/>.
        /// </remarks>
        private static string Fingerprint(string text)
        {
            var hash = 2166136261u;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                hash = (hash ^ (uint)(c & 0xFF)) * 16777619u;
                hash = (hash ^ (uint)((c >> 8) & 0xFF)) * 16777619u;
            }

            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static string ReadPrivate(object target, string field)
        {
            var value = ReadPrivateValue(target, field);
            return value == null ? "<no such field or null>" : (value.ToString() ?? "<null>");
        }

        private static object? ReadPrivateValue(object target, string field)
        {
            var info = target.GetType().GetField(
                field, BindingFlags.Instance | BindingFlags.NonPublic);
            return info == null ? null : info.GetValue(target);
        }

        /// <summary>
        /// Hands this command to Quantum Console.
        /// </summary>
        /// <remarks>
        /// ⚠️ The <c>[Command]</c> attribute alone does nothing in this
        /// assembly — QC's own scan does not reach it, so registration is
        /// explicit and the attribute is documentation. Omitting this call is
        /// green through build, deploy and launch, and fails only with two
        /// instances up and a fight already over.
        /// </remarks>
        internal static void RegisterConsoleCommands()
        {
            var probe = typeof(DebriefProbeGlue).GetMethod(
                nameof(DebriefProbe), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(probe, "pbj.debrief-probe"));
        }
    }
}
