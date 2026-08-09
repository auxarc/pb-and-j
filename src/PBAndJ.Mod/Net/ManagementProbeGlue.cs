using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // THROWAWAY, like OverworldProbeGlue beside it. This one answers the two
    // questions the 2026-08-08 adversarial review left gating M12d, and nothing
    // else. Delete it once both answers are in the design doc.
    //
    //   Q3. Can the management UI be driven from OUTSIDE its own flow? Every
    //       edit measured during the M12 recon was made by a human clicking, and
    //       the review found this gates M12d outright: a satisfied SalvageBarrier
    //       has no way to release anyone unless each machine's salvage screen can
    //       be committed programmatically.
    //
    //   Q2. Does customTags survive the save round trip in practice? It is
    //       read-verified only, and now known to be structurally IMPOSSIBLE for
    //       subsystems (DataBlockSavedSubsystem has no customTags field at all,
    //       eight fields, none of them tags). The parts half is what is left to
    //       confirm, and it is the half M12d would rest on.
    //
    // The reading says Q3's answer is probably "yes, and the game hands you the
    // levers" — which is exactly the kind of confident read this project has been
    // burned by six times, hence a probe rather than a seventh careful read:
    //
    //   * Selection is public static, three ways, entity-level and unit-level:
    //     OnSalvageRecover/OnSalvageScrap/OnSalvageSkip(int entityID) at
    //     CIViewOverworldDebriefing:2026-2036, and the ...Unit(int unitID) trio at
    //     :1868-1878. Each does the WHOLE job — mutates SalvageSelection, redraws
    //     the affected row, calls SalvageRefreshBudget, fires the audio ping — so
    //     a driven selection should be indistinguishable from a clicked one.
    //
    //   * The commit is reachable through OnStageNextExternal() (:760), public
    //     static, which calls OnStageNext(), which at DebriefingStage.Salvage
    //     calls OnSalvageFinish() -> SalvageFinish() -> ProcessSalvageSelections
    //     (:2825). "External" in the name is the game telling us this is the
    //     out-of-flow entry, and it has ZERO callers anywhere in the decompile —
    //     so it is either scene-wired or dead, and which of those it is decides
    //     whether M12d can drive it.
    //
    //   * Note the commit method is SalvageFinish, not "FinishDebriefing" — that
    //     name does not exist in the class. Worth saying because the review used
    //     it and a later grep for it will come back empty.
    //
    // Console return values do NOT reach Player.log — Quantum Console renders them
    // in its own view — so everything worth keeping is Debug.Log'd.
    [ExcludeFromCodeCoverage]
    internal static class ManagementProbeGlue
    {
        private const string Tag = "[pb-and-j] mg-probe";

        /// <summary>
        /// The tag Q2 writes and looks for. Prefixed per the design doc: the
        /// customTags namespace is live and shared with the game's own flags
        /// (CombatDamageSystem reads flag_no_damage and flag_no_loss), so a probe
        /// tag must not be able to collide with one.
        /// </summary>
        private const string ProbeTag = "pbj_probe_owner";

        // --- pbj.mg-probe: read-only, safe from any game state ---

        public static string ManagementProbe()
        {
            var report = new StringBuilder();
            report.Append(Tag).Append('\n');

            Section(report, "debriefing view", ProbeDebriefing);
            Section(report, "salvage entries", ProbeSalvageEntries);
            Section(report, "drive surface", ProbeDriveSurface);
            Section(report, "probe tags", ProbeTags);

            Debug.Log(report.ToString());
            return Tag + " written to the log";
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

        private static void ProbeDebriefing(StringBuilder report)
        {
            var view = CIViewOverworldDebriefing.ins;
            if (view == null)
            {
                report.Append("  CIViewOverworldDebriefing.ins is NULL — never constructed\n");
                return;
            }

            report.Append("  present | entered: ").Append(view.IsEntered()).Append('\n');

            // The stage decides what OnStageNextExternal would DO. Advancing from
            // Salvage commits; advancing from anywhere else just moves a page.
            // Reading it before driving is the difference between a probe and an
            // accident.
            report.Append("  stageLast: ").Append(ReadPrivate(view, "stageLast")).Append('\n');
            report.Append("  salvageBudgetLast: ").Append(ReadPrivate(view, "salvageBudgetLast"))
                  .Append(" | salvageCostTotal: ").Append(ReadPrivate(view, "salvageCostTotal"))
                  .Append(" | salvageCostValid: ").Append(ReadPrivate(view, "salvageCostValid"))
                  .Append('\n');
            report.Append("  outcomeVictoryLast: ").Append(ReadPrivate(view, "outcomeVictoryLast")).Append('\n');

            // CanSave refuses while the debriefing is entered (DataManagerSave:144),
            // so M12c cannot snapshot from inside this screen. Confirm it rather
            // than trust the read.
            report.Append("  DataManagerSave.CanSave(): ").Append(DataManagerSave.CanSave()).Append('\n');
        }

        /// <remarks>
        /// Walks the view's OWN salvageGroups, in the view's own order, because a
        /// flat dump of entity ids is unusable at the keyboard: you cannot tell
        /// which id is the item on screen, so you cannot tell whether the screen
        /// agreed with what you drove. The grid element is keyed by exactly this
        /// entityID (CIHelperGridElementDebriefingSalvage.entityID), grouped by
        /// unitPersistentID, so this listing and the screen are the same list in
        /// the same order.
        ///
        /// Names are resolved the way CIHelperLoadoutEntry:81 resolves them —
        /// textNameProcessed with the blueprint key as fallback — so a row reads
        /// as the item you are looking at rather than as a number.
        ///
        /// Per-entity kind and serial are printed because the review found the
        /// list mixes parts and subsystems and that serial comes from TWO
        /// counters (DataHelperStats:14-16), so a part and a subsystem can hold
        /// the same serial. A collision shows up here as two rows sharing a
        /// serial with different kinds — the measurement that settles whether
        /// (kind, serial) really is the key M12d needs.
        /// </remarks>
        private static void ProbeSalvageEntries(StringBuilder report)
        {
            var view = CIViewOverworldDebriefing.ins;
            var groups = view == null
                ? null
                : ReadPrivateValue(view, "salvageGroups") as System.Collections.IEnumerable;

            if (groups == null)
            {
                report.Append("  no salvageGroups on the view — open a debriefing first\n");
                return;
            }

            var seen = new Dictionary<int, string>();
            var collisions = 0;
            var groupCount = 0;

            foreach (var group in groups)
            {
                if (group == null)
                {
                    continue;
                }

                groupCount++;

                var unitId = Convert.ToInt32(ReadPublicValue(group, "unitPersistentID") ?? -1);
                var multiplier = Convert.ToSingle(ReadPublicValue(group, "costMultiplier") ?? 0f);
                var unit = unitId >= 0 ? IDUtility.GetPersistentEntity(unitId) : null;

                // costMultiplier 0 is the site-inventory group: those items are
                // FREE (EquipmentUtility:1622-1624) and spend no pool at all, so
                // driving one proves the plumbing but proves nothing about the
                // budget. Say which is which rather than let it be discovered.
                report.Append("  unit ").Append(unitId)
                      .Append(" '").Append(unit != null && unit.hasNameInternal ? unit.nameInternal.s : "<unnamed>")
                      .Append("' | costMultiplier ").Append(multiplier.ToString("F1"))
                      .Append(multiplier <= 0f ? "  (FREE — spends no budget)" : "  (spends budget)")
                      .Append('\n');

                var entities = ReadPublicValue(group, "entities") as IEnumerable<EquipmentEntity>;
                if (entities == null)
                {
                    report.Append("    <no entities list>\n");
                    continue;
                }

                foreach (var entity in entities)
                {
                    if (entity == null)
                    {
                        continue;
                    }

                    var kind = entity.isPart ? "part" : entity.isSubsystem ? "subsystem" : "other";
                    var serial = entity.hasSerial ? entity.serial.i : -1;
                    var dismantle = entity.hasSalvageSelection && entity.salvageSelection.dismantle;

                    if (serial >= 0)
                    {
                        if (seen.TryGetValue(serial, out var firstKind) && firstKind != kind)
                        {
                            collisions++;
                        }
                        else
                        {
                            seen[serial] = kind;
                        }
                    }

                    report.Append("    id ").Append(entity.id.id)
                          .Append(" | ").Append(DisplayName(entity))
                          .Append(" | ").Append(kind)
                          .Append(" | serial ").Append(serial)
                          .Append(" | ").Append(DescribeSelection(entity))
                          .Append(" | cost ").Append(SafeCost(entity, dismantle, multiplier))
                          .Append(" | tags ").Append(DescribeTags(entity))
                          .Append('\n');
                }
            }

            report.Append("  groups: ").Append(groupCount)
                  .Append(" | cross-kind serial collisions: ").Append(collisions)
                  .Append(collisions > 0 ? "  <-- (kind, serial) IS the key\n" : "\n");
        }

        /// <remarks>
        /// The screen shows recover / scrap / skip, not a bool — and "skip" is
        /// the ABSENCE of the component, not a third value. Printing the raw
        /// dismantle flag would make skip and recover look identical.
        /// </remarks>
        private static string DescribeSelection(EquipmentEntity entity)
        {
            if (!entity.hasSalvageSelection)
            {
                return "SKIP";
            }

            return entity.salvageSelection.dismantle ? "SCRAP" : "RECOVER";
        }

        /// <remarks>
        /// Same resolution CIHelperLoadoutEntry:81 uses, with the blueprint key
        /// as the fallback. The key alone (wpn_..., part_...) is usually enough
        /// to find the row by eye even when localization is missing.
        /// </remarks>
        private static string DisplayName(EquipmentEntity entity)
        {
            try
            {
                if (entity.isPart && entity.hasDataKeyPartPreset)
                {
                    var key = entity.dataKeyPartPreset.s;
                    var preset = DataMultiLinker<DataContainerPartPreset>.GetEntry(key, false);
                    var name = preset?.textNameProcessed?.s ?? preset?.textName?.s;
                    var rating = entity.hasRating ? $" r{entity.rating.i}" : string.Empty;
                    return (string.IsNullOrEmpty(name) ? key : name) + rating;
                }

                if (entity.isSubsystem && entity.hasDataKeySubsystem)
                {
                    var key = entity.dataKeySubsystem.s;
                    var subsystem = DataMultiLinker<DataContainerSubsystem>.GetEntry(key, false);
                    var name = subsystem?.textNameProcessed?.s ?? subsystem?.textName?.s;
                    return string.IsNullOrEmpty(name) ? key : name!;
                }
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }

            return "<unnamed>";
        }

        /// <remarks>
        /// Reflection presence checks only — nothing is invoked here. A method
        /// that is missing is a refuted plan; a method that is present is only a
        /// promise until pbj.mg-select actually runs it.
        /// </remarks>
        private static void ProbeDriveSurface(StringBuilder report)
        {
            var type = typeof(CIViewOverworldDebriefing);
            foreach (var name in new[]
                     {
                         "OnSalvageRecover", "OnSalvageScrap", "OnSalvageSkip",
                         "OnSalvageRecoverUnit", "OnSalvageScrapUnit", "OnSalvageSkipUnit",
                         "OnStageNextExternal", "OnStageExternal",
                     })
            {
                var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
                report.Append("  ").Append(name).Append(": ")
                      .Append(method == null ? "ABSENT" : "public static, present")
                      .Append('\n');
            }
        }

        private static void ProbeTags(StringBuilder report)
        {
            var equipment = Contexts.sharedInstance.equipment;
            var tagged = equipment.GetGroup(EquipmentMatcher.CustomTags).GetEntities();

            var parts = 0;
            var subsystems = 0;

            report.Append("  entities with CustomTags: ").Append(tagged.Length).Append('\n');

            foreach (var entity in tagged)
            {
                if (entity.customTags?.tags == null || !entity.customTags.tags.Contains(ProbeTag))
                {
                    continue;
                }

                if (entity.isPart)
                {
                    parts++;
                }
                else if (entity.isSubsystem)
                {
                    subsystems++;
                }

                report.Append("    TAGGED id ").Append(entity.id.id)
                      .Append(" | ").Append(entity.isPart ? "part" : entity.isSubsystem ? "subsystem" : "other")
                      .Append(" | serial ").Append(entity.hasSerial ? entity.serial.i : -1)
                      .Append('\n');
            }

            report.Append("  carrying ").Append(ProbeTag)
                  .Append(" | parts: ").Append(parts)
                  .Append(" | subsystems: ").Append(subsystems)
                  .Append('\n');
        }

        // --- pbj.mg-select: Q3, the selection half, non-destructive ---

        /// <summary>
        /// Drives one salvage decision through the game's own public static entry
        /// point and reports whether the screen's own budget arithmetic moved.
        /// </summary>
        /// <param name="entityId">Equipment entity id, as listed by pbj.mg-probe.</param>
        /// <param name="decision">recover | scrap | skip.</param>
        /// <remarks>
        /// Reversible: run it again with the original decision to put the entry
        /// back. Nothing is committed until the stage advances, so this is the
        /// half of Q3 that can be answered without risking a save.
        /// </remarks>
        public static string ManagementSelect(int entityId, string decision)
        {
            var view = CIViewOverworldDebriefing.ins;
            if (view == null)
            {
                return Tag + " CIViewOverworldDebriefing.ins is NULL — open a debriefing first";
            }

            var entity = IDUtility.GetEquipmentEntity(entityId);
            if (entity == null)
            {
                return $"{Tag} no equipment entity with id {entityId} — run pbj.mg-probe for the list";
            }

            var costBefore = ReadPrivate(view, "salvageCostTotal");
            var selectionBefore = entity.hasSalvageSelection
                ? entity.salvageSelection.dismantle.ToString()
                : "<none>";

            switch (decision?.ToLowerInvariant())
            {
                case "recover":
                    CIViewOverworldDebriefing.OnSalvageRecover(entityId);
                    break;
                case "scrap":
                    CIViewOverworldDebriefing.OnSalvageScrap(entityId);
                    break;
                case "skip":
                    CIViewOverworldDebriefing.OnSalvageSkip(entityId);
                    break;
                default:
                    return Tag + " decision must be recover, scrap or skip";
            }

            var costAfter = ReadPrivate(view, "salvageCostTotal");
            var selectionAfter = entity.hasSalvageSelection
                ? entity.salvageSelection.dismantle.ToString()
                : "<none>";

            // The screen updating is the whole answer. An ECS-only change with a
            // stale on-screen budget would mean the UI has flow state the entry
            // point does not touch — which is precisely the failure M12d cannot
            // afford, and is why this reports the view's field rather than
            // recomputing the cost itself.
            var line = $"{Tag} select | id {entityId} | {decision}"
                     + $" | selection {selectionBefore} -> {selectionAfter}"
                     + $" | view cost {costBefore} -> {costAfter}"
                     + $" | budget {ReadPrivate(view, "salvageBudgetLast")}"
                     + $" | costValid {ReadPrivate(view, "salvageCostValid")}";

            Debug.Log(line);
            return line + " | check the screen itself agrees";
        }

        // --- pbj.mg-select-unit: Q3, the same question, easier to see ---

        /// <summary>
        /// Drives a whole unit's salvage row through
        /// CIViewOverworldDebriefing.OnSalvageRecoverUnit / ScrapUnit / SkipUnit.
        /// </summary>
        /// <param name="unitId">Unit persistent id, as listed by pbj.mg-probe.</param>
        /// <param name="decision">recover | scrap | skip.</param>
        /// <remarks>
        /// Preferred over the per-entity form for the first run: a whole row
        /// flipping is unmistakable on screen, where one item among many is easy
        /// to misread. It also takes a different code path — the unit trio at
        /// :1868-1878 goes through OnSalvageDecisionUnit, which calls
        /// RefreshVisibleArea on the whole grid rather than SalvageRedrawIsolated
        /// — so the two commands answer the question for both redraw routes.
        /// </remarks>
        public static string ManagementSelectUnit(int unitId, string decision)
        {
            var view = CIViewOverworldDebriefing.ins;
            if (view == null)
            {
                return Tag + " CIViewOverworldDebriefing.ins is NULL — open a debriefing first";
            }

            var costBefore = ReadPrivate(view, "salvageCostTotal");

            switch (decision?.ToLowerInvariant())
            {
                case "recover":
                    CIViewOverworldDebriefing.OnSalvageRecoverUnit(unitId);
                    break;
                case "scrap":
                    CIViewOverworldDebriefing.OnSalvageScrapUnit(unitId);
                    break;
                case "skip":
                    CIViewOverworldDebriefing.OnSalvageSkipUnit(unitId);
                    break;
                default:
                    return Tag + " decision must be recover, scrap or skip";
            }

            // The game logs "Couldn't find salvage metadata group for unit ID X"
            // and returns quietly when the id is not a salvage group (:1891), so
            // an unchanged cost with no visible change means a bad id, not a
            // refuted mechanism. Say so rather than let a typo read as a finding.
            var line = $"{Tag} select-unit | unit {unitId} | {decision}"
                     + $" | view cost {costBefore} -> {ReadPrivate(view, "salvageCostTotal")}"
                     + $" | budget {ReadPrivate(view, "salvageBudgetLast")}"
                     + $" | costValid {ReadPrivate(view, "salvageCostValid")}";

            Debug.Log(line);
            return line + " | nothing changed? check the log for the game's 'couldn't find salvage metadata group'";
        }

        // --- pbj.mg-advance: Q3, the commit half, DESTRUCTIVE at Salvage ---

        /// <summary>
        /// Calls CIViewOverworldDebriefing.OnStageNextExternal(). At
        /// DebriefingStage.Salvage this COMMITS the salvage — it is the same call
        /// the Finish button makes — so it refuses without an explicit confirm.
        /// </summary>
        /// <param name="confirm">Must be "yes" to advance out of the Salvage stage.</param>
        public static string ManagementAdvance(string confirm)
        {
            var view = CIViewOverworldDebriefing.ins;
            if (view == null)
            {
                return Tag + " CIViewOverworldDebriefing.ins is NULL — open a debriefing first";
            }

            var stage = ReadPrivate(view, "stageLast");
            var committing = string.Equals(stage, "Salvage", StringComparison.Ordinal);

            if (committing && !string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return $"{Tag} stage is Salvage — this COMMITS salvage and cannot be undone."
                     + " Re-run as: pbj.mg-advance yes";
            }

            Debug.Log($"{Tag} advance | stage {stage} | committing {committing}"
                    + $" | budget {ReadPrivate(view, "salvageBudgetLast")}"
                    + $" | cost {ReadPrivate(view, "salvageCostTotal")}"
                    + $" | costValid {ReadPrivate(view, "salvageCostValid")}");

            CIViewOverworldDebriefing.OnStageNextExternal();

            var line = $"{Tag} advance done | stage {stage} -> {ReadPrivate(view, "stageLast")}"
                     + $" | entered {view.IsEntered()}";

            Debug.Log(line);

            // If the stage did not move, OnStageNextExternal is scene-wired to
            // nothing reachable from here, or an early-out swallowed it (tutorial
            // open, an animation still running, or !salvageCostValid). All three
            // are logged by the game itself as warnings — read Player.log, not
            // just this line.
            return line + " | if the stage did not move, check Player.log for the game's own warning";
        }

        // --- pbj.mg-confirm: the last link in Q3's chain ---

        /// <summary>
        /// Presses the confirm button on whatever CIViewDialogConfirmation is
        /// currently open. At the salvage screen that dialog's confirm callback
        /// IS SalvageFinish, so this commits.
        /// </summary>
        /// <remarks>
        /// OnStageNextExternal turned out to drive the screen all the way to the
        /// game's own confirmation modal (OnSalvageFinish:2762 opens it with
        /// SalvageFinish as callbackOnConfirm), which means the commit is gated
        /// behind a dialog rather than reachable directly. That is not an
        /// obstacle: CIViewDialogConfirmation.OnConfirm is private, but the
        /// button's callbackOnClick is a plain public UICallback and
        /// UICallback.Invoke() is public — so the last link needs no reflection
        /// into a private method, just the same field the button itself uses.
        ///
        /// Deliberately separate from mg-advance rather than chained onto it: the
        /// two are different findings, and a probe that silently confirmed its
        /// own dialog would prove one step while hiding the other.
        /// </remarks>
        public static string ManagementConfirm(string confirm)
        {
            var dialog = CIViewDialogConfirmation.ins;
            if (dialog == null)
            {
                return Tag + " CIViewDialogConfirmation.ins is NULL";
            }

            if (!dialog.IsEntered())
            {
                return Tag + " no confirmation dialog is open";
            }

            if (!string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return $"{Tag} this presses CONFIRM on the open dialog — at the salvage screen that"
                     + " commits and cannot be undone. Re-run as: pbj.mg-confirm yes";
            }

            var callback = dialog.buttonConfirm?.callbackOnClick;
            if (callback == null)
            {
                return Tag + " buttonConfirm has no callbackOnClick — nothing to press";
            }

            Debug.Log($"{Tag} confirm | pressing the open dialog's confirm callback");
            callback.Invoke();

            var line = $"{Tag} confirm done | dialog entered {dialog.IsEntered()}"
                     + $" | debriefing entered {(CIViewOverworldDebriefing.ins != null && CIViewOverworldDebriefing.ins.IsEntered())}";

            Debug.Log(line);

            // The game's own proof that the commit ran is its warning line
            // "Attempting to transfer salvage from ... | Last budget: N".
            return line + " | look for the game's 'Attempting to transfer salvage' line in Player.log";
        }

        // --- pbj.mg-serials: is the serial a durable key for subsystems? ---

        /// <summary>
        /// Prints a comparable fingerprint of every part and subsystem serial, so
        /// the same command before and after a save/load says whether serials are
        /// stable across the round trip.
        /// </summary>
        /// <remarks>
        /// This is the measurement the subsystem ownership problem now turns on.
        /// customTags cannot carry a subsystem's owner — measured, 606 in and 0
        /// out — but DataBlockSavedSubsystem DOES persist serial, and the restore
        /// passes it straight back into CreateSubsystemEntity
        /// (DataManagerSave.cs:1905). If serials really are stable, a side table
        /// keyed by (kind, serial) recovers everything the tag would have carried.
        /// If they are not, subsystem ownership needs a different idea entirely.
        /// </remarks>
        public static string ManagementSerials()
        {
            var report = new StringBuilder();
            report.Append(Tag).Append(" serials\n");

            var parts = new List<int>();
            var subsystems = new List<int>();

            foreach (var entity in Contexts.sharedInstance.equipment.GetEntities())
            {
                if (!entity.hasSerial)
                {
                    continue;
                }

                if (entity.isPart)
                {
                    parts.Add(entity.serial.i);
                }
                else if (entity.isSubsystem)
                {
                    subsystems.Add(entity.serial.i);
                }
            }

            Fingerprint(report, "parts", parts);
            Fingerprint(report, "subsystems", subsystems);

            Debug.Log(report.ToString());
            return Tag + " serials written to the log — run again after a save/load and compare";
        }

        /// <remarks>
        /// Count, min, max and sum together catch every failure that matters: a
        /// wholesale re-mint moves all four, a partial re-mint moves sum and max,
        /// and a stable round trip moves none. Cheaper to compare by eye than a
        /// thousand-line serial dump, and it does not depend on ordering.
        /// </remarks>
        private static void Fingerprint(StringBuilder report, string label, List<int> serials)
        {
            if (serials.Count == 0)
            {
                report.Append("  ").Append(label).Append(": none\n");
                return;
            }

            serials.Sort();

            long sum = 0;
            foreach (var serial in serials)
            {
                sum += serial;
            }

            report.Append("  ").Append(label)
                  .Append(": count ").Append(serials.Count)
                  .Append(" | min ").Append(serials[0])
                  .Append(" | max ").Append(serials[serials.Count - 1])
                  .Append(" | sum ").Append(sum)
                  .Append('\n');
        }

        // --- pbj.mg-tag / pbj.mg-untag: Q2, the customTags round trip ---

        /// <summary>
        /// Writes the probe tag onto every part currently in the player base
        /// inventory, and onto every subsystem too — deliberately, because the
        /// subsystem half is the one predicted to VANISH across a save.
        /// </summary>
        /// <remarks>
        /// The run: pbj.mg-tag, then save, then load that save, then pbj.mg-probe.
        /// Parts should still carry the tag; subsystems should not, because
        /// DataBlockSavedSubsystem has nowhere to put it. Tagging both is what
        /// makes the negative result evidence rather than an absence.
        /// </remarks>
        public static string ManagementTag()
        {
            var equipment = Contexts.sharedInstance.equipment;
            var parts = 0;
            var subsystems = 0;

            foreach (var entity in equipment.GetEntities())
            {
                if (!entity.isPart && !entity.isSubsystem)
                {
                    continue;
                }

                // Replace, never clobber: the namespace is shared with the game's
                // own flags and a probe that eats flag_no_damage would be a real
                // bug wearing a probe's clothes.
                var tags = entity.hasCustomTags && entity.customTags.tags != null
                    ? new HashSet<string>(entity.customTags.tags)
                    : new HashSet<string>();

                tags.Add(ProbeTag);
                entity.ReplaceCustomTags(tags);

                if (entity.isPart)
                {
                    parts++;
                }
                else
                {
                    subsystems++;
                }
            }

            var line = $"{Tag} tagged with {ProbeTag} | parts {parts} | subsystems {subsystems}"
                     + " | now save, load that save, and run pbj.mg-probe";
            Debug.Log(line);
            return line;
        }

        /// <summary>Removes the probe tag, leaving any other tags in place.</summary>
        public static string ManagementUntag()
        {
            var equipment = Contexts.sharedInstance.equipment;
            var cleared = 0;

            foreach (var entity in equipment.GetGroup(EquipmentMatcher.CustomTags).GetEntities())
            {
                if (entity.customTags?.tags == null || !entity.customTags.tags.Contains(ProbeTag))
                {
                    continue;
                }

                var tags = new HashSet<string>(entity.customTags.tags);
                tags.Remove(ProbeTag);
                entity.ReplaceCustomTags(tags);
                cleared++;
            }

            var line = $"{Tag} cleared {ProbeTag} from {cleared} entities";
            Debug.Log(line);
            return line;
        }

        // --- helpers ---

        /// <remarks>
        /// Priced at the group's OWN multiplier rather than a hardcoded 1f, so a
        /// free site-inventory row reads as 0 here exactly as it does on screen.
        /// </remarks>
        private static string SafeCost(EquipmentEntity entity, bool dismantle, float multiplier)
        {
            try
            {
                return EquipmentUtility.GetSalvageCost(entity, dismantle, multiplier).ToString();
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }
        }

        private static string DescribeTags(EquipmentEntity entity)
        {
            if (!entity.hasCustomTags || entity.customTags.tags == null || entity.customTags.tags.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", entity.customTags.tags.ToArray());
        }

        /// <remarks>
        /// Every field this probe wants is private, and all of them are the
        /// screen's own arithmetic rather than ECS state — which is the point:
        /// recomputing the budget ourselves would answer a different question
        /// than "did the screen notice".
        /// </remarks>
        private static string ReadPrivate(object target, string field)
        {
            try
            {
                var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                if (info == null)
                {
                    return "<no such field>";
                }

                return info.GetValue(target)?.ToString() ?? "<null>";
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }
        }

        private static object? ReadPrivateValue(object target, string field)
        {
            try
            {
                return target.GetType()
                             .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                             ?.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <remarks>
        /// SalvageGroupData is a public nested class but reaching it by name from
        /// the mod would bind us to its shape; reading its public fields
        /// reflectively keeps a throwaway probe from becoming a compile-time
        /// dependency on a private screen's internals.
        /// </remarks>
        private static object? ReadPublicValue(object target, string field)
        {
            try
            {
                return target.GetType()
                             .GetField(field, BindingFlags.Instance | BindingFlags.Public)
                             ?.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void RegisterConsoleCommands()
        {
            var probe = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementProbe), BindingFlags.Static | BindingFlags.Public);
            var select = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementSelect), BindingFlags.Static | BindingFlags.Public);
            var selectUnit = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementSelectUnit), BindingFlags.Static | BindingFlags.Public);
            var advance = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementAdvance), BindingFlags.Static | BindingFlags.Public);
            var confirm = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementConfirm), BindingFlags.Static | BindingFlags.Public);
            var serials = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementSerials), BindingFlags.Static | BindingFlags.Public);
            var tag = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementTag), BindingFlags.Static | BindingFlags.Public);
            var untag = typeof(ManagementProbeGlue).GetMethod(nameof(ManagementUntag), BindingFlags.Static | BindingFlags.Public);

            QuantumConsoleProcessor.TryAddCommand(new CommandData(probe, "pbj.mg-probe"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(select, "pbj.mg-select"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(selectUnit, "pbj.mg-select-unit"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(advance, "pbj.mg-advance"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(confirm, "pbj.mg-confirm"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(serials, "pbj.mg-serials"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(tag, "pbj.mg-tag"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(untag, "pbj.mg-untag"));
        }
    }
}
