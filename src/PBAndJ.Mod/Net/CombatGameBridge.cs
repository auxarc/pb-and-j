using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Humble-object glue: the entire ECS surface Core needs, expressed without
    // Core ever seeing a game type. No logic lives here beyond field copying
    // and the guards the game itself requires.
    [ExcludeFromCodeCoverage]
    internal sealed class CombatGameBridge : IPbjGameBridge
    {
        /// <summary>Read by the execute-button patches.</summary>
        internal static bool ExecutionLocked { get; private set; }

        /// <summary>
        /// True while WE are inside ConfirmExecution. The external-advance
        /// detector must not fire on our own barrier-driven commit.
        /// </summary>
        internal static bool CommitInProgress { get; private set; }

        internal static void ResetLock()
        {
            ExecutionLocked = false;
        }

        public int CurrentTurn
        {
            get
            {
                var combat = Contexts.sharedInstance.combat;
                // currentTurn throws when the component is absent, which it is
                // outside combat.
                return combat.hasCurrentTurn ? combat.currentTurn.i : -1;
            }
        }

        public bool InCombat =>
            IDUtility.IsGameState("combat") && Contexts.sharedInstance.combat.hasCurrentTurn;

        public IReadOnlyList<string> AssignableUnitNames
        {
            get
            {
                var names = new List<string>();
                if (!InCombat)
                {
                    return names;
                }
                foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
                {
                    // Player-controllable AND friendly: friendly alone would
                    // include scenario-scripted AI allies, whose orders would
                    // fight the AI planning systems.
                    if (!unit.isPlayerControllable || !CombatUIUtility.IsUnitFriendly(unit))
                    {
                        continue;
                    }
                    var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                    if (persistent != null && persistent.hasNameInternal)
                    {
                        names.Add(persistent.nameInternal.s);
                    }
                }
                return names;
            }
        }

        public IReadOnlyList<OrderPayload> CaptureLocalOrders()
        {
            var orders = new List<OrderPayload>();
            if (!InCombat)
            {
                return orders;
            }

            // Same group query and skip predicate as the M2 action dump.
            var group = Contexts.sharedInstance.action.GetGroup(ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed));

            foreach (var action in group.GetEntities())
            {
                if (action.CompletedAction || action.isDisposed || action.AIAction)
                {
                    continue;
                }
                var order = OrderMapper.Capture(action);
                if (order != null)
                {
                    orders.Add(order);
                }
            }
            return orders;
        }

        public OrderApplyResult ApplyOrder(OrderPayload order)
        {
            return InCombat ? OrderMapper.Apply(order) : OrderApplyResult.UnknownUnit;
        }

        public bool CommitTurn()
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                return false;
            }

            var before = combat.currentTurn.i;
            CommitInProgress = true;
            try
            {
                CombatUtilities.ConfirmExecution(1);
            }
            finally
            {
                CommitInProgress = false;
            }

            // ConfirmExecution is void and refuses silently in four normal
            // situations, so the only honest test is whether the turn moved.
            var after = Contexts.sharedInstance.combat.currentTurn.i;
            return after != before;
        }

        public void SetExecutionLocked(bool locked)
        {
            ExecutionLocked = locked;
            if (!InCombat)
            {
                return;
            }
            // Never write isScenarioAllowingExecution directly on unlock — let
            // the game recompute the real scenario-derived value.
            ScenarioUtility.RecheckExecutionAvailability(forceUIRefresh: true);
        }

        // The digest is a projection of the snapshot, never an independent walk.
        // If the two were allowed to disagree about which units exist, a client
        // would fail its post-correction check for reasons that have nothing to
        // do with correction.
        public string ComputeStateDigest()
        {
            var snapshot = CaptureSnapshot();
            var units = new UnitState[snapshot.Count];
            for (var i = 0; i < snapshot.Count; i++)
            {
                units[i] = snapshot[i].ToUnitState();
            }
            return StateDigest.Compute(units);
        }

        public IReadOnlyList<UnitSnapshot> CaptureSnapshot()
        {
            var units = new List<UnitSnapshot>();
            if (!InCombat)
            {
                return units;
            }

            // Every unit with a resolvable name — hostiles included, not just the
            // assignable ones. A client must be corrected about the whole fight.
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }

                var position = unit.hasPosition ? unit.position.v : Vector3.zero;
                var rotation = unit.hasRotation ? unit.rotation.q : Quaternion.identity;
                var facing = unit.hasFacing ? unit.facing.v : Vector3.forward;
                var integrity = persistent.hasUnitFrameIntegrity ? persistent.unitFrameIntegrity.f : 0f;
                var dead = persistent.hasDeathStatus;

                units.Add(new UnitSnapshot(
                    persistent.nameInternal.s,
                    new Vec3(position.x, position.y, position.z),
                    new Vec4(rotation.x, rotation.y, rotation.z, rotation.w),
                    new Vec3(facing.x, facing.y, facing.z),
                    integrity,
                    dead,
                    dead ? persistent.deathStatus.time : 0f));

                if (units.Count == PbjMessageCodec.MaxUnitsPerSnapshot)
                {
                    // Clamp at capture rather than letting the encoder produce a
                    // frame the far side would reject outright. Loud, because a
                    // silently truncated snapshot reads as a correct one.
                    Debug.LogWarning(NetLog.SnapshotClamped(
                        units.Count, PbjMessageCodec.MaxUnitsPerSnapshot));
                    break;
                }
            }
            return units;
        }

        // Safe only because a client never sets combat.Simulating, so no playback
        // system is driving these transforms and nothing overwrites the write on
        // the next tick. The same call on a simulating host would lose.
        public void ApplySnapshot(IReadOnlyList<UnitSnapshot> snapshot)
        {
            if (!InCombat || snapshot.Count == 0)
            {
                return;
            }

            var byName = new Dictionary<string, UnitSnapshot>(snapshot.Count);
            for (var i = 0; i < snapshot.Count; i++)
            {
                var name = snapshot[i].Name;
                if (!string.IsNullOrEmpty(name))
                {
                    byName[name!] = snapshot[i];
                }
            }

            var localOnly = 0;
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                if (!byName.TryGetValue(persistent.nameInternal.s, out var state))
                {
                    localOnly++;
                    continue;
                }

                // Components only, and that is sufficient to render: PositionLinkSystem
                // and RotationLinkSystem are reactive on CombatMatcher.Position /
                // .Rotation and call CombatView.OnPosition/OnRotation, which set
                // the view transform. Neither is gated on the simulation running,
                // so a correction arriving between turns is visible immediately.
                unit.ReplacePosition(new Vector3(state.Position.X, state.Position.Y, state.Position.Z));
                unit.ReplaceRotation(new Quaternion(
                    state.Rotation.X, state.Rotation.Y, state.Rotation.Z, state.Rotation.W));
                unit.ReplaceFacing(new Vector3(state.Facing.X, state.Facing.Y, state.Facing.Z));
                persistent.ReplaceUnitFrameIntegrity(state.Integrity);

                if (state.IsDead && !persistent.hasDeathStatus)
                {
                    persistent.ReplaceDeathStatus(state.DeathTime, "remote");
                }
                byName.Remove(persistent.nameInternal.s);
            }

            // Entities are never created from a snapshot. A roster difference is
            // a structural mismatch that hard-setting positions cannot fix, so
            // it is reported rather than papered over.
            if (byName.Count > 0 || localOnly > 0)
            {
                Debug.Log(NetLog.SnapshotUnitsSkipped(byName.Count, localOnly));
            }
        }

        // Walks the game's own replay recorder and re-keys it for the wire.
        //
        // Three things here are not obvious and were each verified against the
        // decompiled 2.2.2-b8339 source:
        //
        // 1. Do NOT gate on CombatReplayHelper.IsRecordingAllowed(). It is
        //    already false by the time we run: OnExecutionEnd clears the flag,
        //    and it is called from CombatUILinkSimulationEnd, which sits in
        //    CombatUISystems (slot 72) — ahead of CombatExecutionEndLateSystem
        //    (slot 93), the system whose postfix brings us here. Both react to
        //    the same Simulating.Removed() collector. Gating on it would return
        //    empty every single turn.
        //
        // 2. The tracks are NOT cleared between turns. experimentalMode is true
        //    by default and OnExecutionStart only clears `units` when it is
        //    false, so a track accumulates for the whole combat. We slice from
        //    the key OnExecutionStart wrote, BY INDEX — not by comparing against
        //    turnStartTime, which is Mathf.RoundToInt'd and so can be *later*
        //    than the previous turn's final key, dragging it into our window.
        //
        // 3. The recorder's last key is not the unit's final position.
        //    OnExecutionEnd samples position before CombatExecutionEndLateSystem
        //    force-sets it onto the projected path, and its own OnUnitSnapshot
        //    call is a no-op by (1). So we append a final key ourselves, read
        //    exactly where CaptureSnapshot reads, which is what makes
        //    "last key == snapshot" true rather than merely hoped for.
        public KeyframeCapture CaptureKeyframes()
        {
            if (!InCombat || CombatReplayHelper.units == null || CombatReplayHelper.units.Count == 0)
            {
                Debug.Log(NetLog.KeyframesUnavailable());
                return KeyframeCapture.None;
            }

            var windowStart = CombatReplayHelper.turnStartTime;
            var windowEnd = Contexts.sharedInstance.combat.hasSimulationTime
                ? Contexts.sharedInstance.combat.simulationTime.f
                : windowStart;

            var tracks = new List<UnitTrack>();
            var clamped = 0;

            foreach (var entry in CombatReplayHelper.units)
            {
                // The recorder keys by combatEntity.id.id, a process-local ECS
                // id that means nothing in another process. Same lookup
                // OnExecutionEnd itself uses.
                var unit = IDUtility.GetCombatEntity(entry.Key);
                if (unit == null || unit.isDestroyed)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }

                var keys = SliceTurn(entry.Value.keyframesTransform, windowStart);

                // The final key, from the same read CaptureSnapshot performs.
                keys.Add(new TransformKey(
                    windowEnd,
                    ToVec3(unit.hasPosition ? unit.position.v : Vector3.zero),
                    ToVec4(unit.hasRotation ? unit.rotation.q : Quaternion.identity)));

                if (keys.Count > PbjMessageCodec.MaxKeysPerTrack)
                {
                    // Drop interior keys and keep the endpoints: a track
                    // truncated at the tail would end playback short of the
                    // state the snapshot already corrected everyone to.
                    clamped++;
                    keys = Decimate(keys, PbjMessageCodec.MaxKeysPerTrack);
                }

                tracks.Add(new UnitTrack(persistent.nameInternal.s, keys));

                if (tracks.Count == PbjMessageCodec.MaxTracksPerKeyframes)
                {
                    Debug.LogWarning(NetLog.KeyframesClamped(
                        CombatReplayHelper.units.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
                    return new KeyframeCapture(windowStart, windowEnd, tracks);
                }
            }

            if (clamped > 0)
            {
                Debug.LogWarning(NetLog.KeyframesClamped(
                    tracks.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
            }
            return new KeyframeCapture(windowStart, windowEnd, tracks);
        }

        // Slices to the current turn by index. The key OnExecutionStart wrote is
        // the first one at or after turnStartTime; everything before it belongs
        // to an earlier turn. Scanning backwards from the end finds the boundary
        // without walking the whole accumulated combat.
        private static List<TransformKey> SliceTurn(
            List<ReplayKeyframeTransform> recorded, float windowStart)
        {
            var first = recorded.Count;
            while (first > 0 && recorded[first - 1].time >= windowStart)
            {
                first--;
            }

            var keys = new List<TransformKey>(recorded.Count - first + 1);
            for (var i = first; i < recorded.Count; i++)
            {
                keys.Add(new TransformKey(
                    recorded[i].time, ToVec3(recorded[i].position), ToVec4(recorded[i].rotation)));
            }
            return keys;
        }

        // Keeps the first and last key and thins what is between them, so a long
        // turn loses temporal resolution rather than its ending.
        private static List<TransformKey> Decimate(List<TransformKey> keys, int cap)
        {
            var kept = new List<TransformKey>(cap) { keys[0] };
            var interior = cap - 2;
            var step = (keys.Count - 2) / (double)interior;
            for (var i = 0; i < interior; i++)
            {
                kept.Add(keys[1 + (int)(i * step)]);
            }
            kept.Add(keys[keys.Count - 1]);
            return kept;
        }

        private static Vec3 ToVec3(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static Vec4 ToVec4(Quaternion q) => new Vec4(q.x, q.y, q.z, q.w);

        // Host-only bridge: a host never plays back, it simulates.
        public void PlayKeyframes(int turn, KeyframeCapture capture)
        {
            KeyframePlayer.Play(turn, capture);
        }

        public void StopKeyframes()
        {
            KeyframePlayer.Stop();
        }

        /// <summary>
        /// Puts this machine's mobile base where the host's is. M12a.
        /// </summary>
        /// <remarks>
        /// The game's own teleport recipe, cribbed from
        /// <c>ConsoleCommandsOverworld:893-901</c> and proven by <c>pbj.ow-mirror</c>
        /// during the recon rather than invented here. Every step earns its
        /// place, and the recipe is the whole reason this is not two lines:
        /// <list type="bullet">
        ///   <item><c>StopMovement</c> — or the client's own path fights the write.</item>
        ///   <item><c>ReplacePosition</c> — the authoritative value.</item>
        ///   <item><c>ReplacePositionTarget</c> — <b>not optional.</b>
        ///   <c>OverworldMovementSystem</c> drags position back toward a stale
        ///   target whenever the clock runs, so a mirror without it snaps back.</item>
        ///   <item><c>isPositionUnchecked</c> — hands the height to
        ///   <c>OverworldPositionValidationSystem</c>, which snaps to this
        ///   machine's own ground. That is why no Y crosses the wire.</item>
        ///   <item>A <b>same-value</b> <c>ReplaceSimulationTime</c> — Entitas
        ///   raises the replaced event with no value-equality short-circuit, so
        ///   this wakes every <c>SimulationTime</c> collector at a delta of zero.
        ///   <c>OverworldRangeSystem</c> is the one that matters: it copies
        ///   Position into PositionDetectedLast, which is what the renderer
        ///   actually draws.</item>
        /// </list>
        /// <b>Never write the host's time value here.</b> Roughly twenty systems
        /// collect on that component and a real delta would run all of them on a
        /// machine that is not simulating — the overworld cousin of the standing
        /// rule against advancing <c>combat.simulationTime</c> on a client.
        /// <para>
        /// In game state <c>basecrawler</c> the write lands and does not render,
        /// because the feeder above runs only in <c>overworld</c>. That is
        /// measured-correct, not a bug to work around: the position is already
        /// right when the player returns to the map.
        /// </para>
        /// </remarks>
        public void MirrorBase(float x, float z)
        {
            var playerBase = IDUtility.playerBaseOverworld;
            if (playerBase == null || !playerBase.hasPosition)
            {
                return;
            }

            // Keep our own Y. The snap below corrects it against local ground,
            // and starting from the current height means an unremarkable
            // correction rather than a fall from wherever the host stands.
            var target = new Vector3(x, playerBase.position.v.y, z);

            PhantomBrigade.Overworld.OverworldUtility.StopMovement(playerBase);
            playerBase.ReplacePosition(target);
            playerBase.ReplacePositionTarget(target);
            playerBase.isPositionUnchecked = true;

            var overworld = Contexts.sharedInstance.overworld;
            if (overworld.hasSimulationTime)
            {
                overworld.ReplaceSimulationTime(overworld.simulationTime.f);
            }
        }

        public void ClearLocalOrders()
        {
            if (!InCombat)
            {
                return;
            }

            // A client's planned orders never execute, because it never
            // simulates. Left alone they accumulate and CaptureLocalOrders starts
            // re-submitting orders the host already ran.
            var matcher = ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed);
            foreach (var action in Contexts.sharedInstance.action.GetGroup(matcher).GetEntities())
            {
                if (action.CompletedAction || action.isDisposed || action.AIAction)
                {
                    continue;
                }
                action.isDisposed = true;
            }
        }

        // --- scenario transfer (M9) ---
        //
        // The save directory the game itself writes: SavedGames/<name>/ holding
        // content.zip and metadata.yaml. Resolved through the game's own
        // DataManagerSave.GetSaveFolderPath rather than a composed path, so this
        // works unchanged on Windows and under Proton, where the same logical
        // folder lives somewhere quite different.

        public ScenarioPayload ReadScenario(string? saveKey)
        {
            try
            {
                var folder = SaveFolder(saveKey);
                if (folder == null || !Directory.Exists(folder))
                {
                    return ScenarioPayload.None;
                }

                // Content is split into parts only when it has to be — M11e. Every
                // save measured is far under one part, so the common case still
                // sends a single content.zip exactly as M9 did. Splitting here
                // rather than at the session keeps the wire-size decision in one
                // place: PbjWriter throws on an oversize blob and PbjRuntime.SendTo
                // does not guard encoding, so nothing above may hand it one.
                var files = new List<ScenarioFile>();
                var contentPath = Path.Combine(folder, ScenarioPayload.ContentFileName);
                if (File.Exists(contentPath))
                {
                    files.AddRange(ScenarioPayload.SplitContent(File.ReadAllBytes(contentPath)));
                }

                var metadataPath = Path.Combine(folder, ScenarioPayload.MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    files.Add(new ScenarioFile(
                        ScenarioPayload.MetadataFileName, File.ReadAllBytes(metadataPath)));
                }

                // A partial directory is handed over as-is rather than patched
                // up here: ScenarioPayload.Inspect is the single place that
                // decides what is sendable, and duplicating that judgement in the
                // glue is how the two drift apart.
                return files.Count == 0
                    ? ScenarioPayload.None
                    : new ScenarioPayload(saveKey, files);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not read the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                return ScenarioPayload.None;
            }
        }

        public bool WriteScenario(ScenarioPayload payload)
        {
            // The destination now travels with the payload — M11e. SaveFolder
            // refuses anything outside the namespace, so a forged key fails here
            // rather than composing a path.
            var folder = SaveFolder(payload.SaveName);
            if (folder == null)
            {
                Debug.LogWarning("[pb-and-j] no writable save folder for '"
                    + payload.SaveName + "' — cannot write the save");
                return false;
            }

            // Staged beside the destination and moved into place, so an
            // interrupted or failed write cannot leave a half-save for
            // pbj.combat-load to find and try to enter.
            var staging = folder + ".pbj-incoming";
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);

                // Split content is reassembled here, never written out as parts:
                // the parts are a wire concern and the game must find the ordinary
                // content.zip it wrote. JoinContent orders by part index rather
                // than by arrival, because the digest is order-independent and
                // nothing promises the wire preserved file order.
                for (var i = 0; i < payload.Files.Count; i++)
                {
                    var file = payload.Files[i];
                    // Belt and braces. The session already refused anything that
                    // is not allowlisted, but this is the statement that actually
                    // composes a path, so it is the one that has to be safe on
                    // its own terms.
                    if (!ScenarioPayload.IsAllowedName(file.Name))
                    {
                        Debug.LogWarning("[pb-and-j] refusing to write scenario file '"
                            + file.Name + "' — not an allowed name");
                        Directory.Delete(staging, true);
                        return false;
                    }
                    if (ScenarioPayload.PartIndex(file.Name) >= 0)
                    {
                        continue;
                    }
                    if (string.Equals(file.Name, ScenarioPayload.ContentFileName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    File.WriteAllBytes(Path.Combine(staging, file.Name), file.Content);
                }

                File.WriteAllBytes(
                    Path.Combine(staging, ScenarioPayload.ContentFileName),
                    ScenarioPayload.JoinContent(payload));

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
                Directory.Move(staging, folder);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not write the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }
                catch (Exception cleanup)
                {
                    Debug.LogWarning("[pb-and-j] could not clean up '" + staging + "': "
                        + cleanup.GetType().Name);
                }
                return false;
            }
        }

        /// <summary>
        /// Starts loading a campaign save. M11d.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="LoadGlue"/>, which owns the pre-checks and the
        /// completion callback. Kept out of this class because the bridge is
        /// otherwise all ECS reads and writes, and a load is neither — it tears
        /// the ECS down and builds a new one.
        /// </remarks>
        public LoadOutcome? BeginLoad(string? saveKey, int selectionVersion, string? saveDigest) =>
            LoadGlue.Begin(saveKey, selectionVersion, saveDigest);

        /// <summary>
        /// Where this save lives, from the game's own path resolution. The
        /// directory name is always ours — never the one on the wire.
        /// </summary>
        /// <summary>
        /// Where a save lives, from the game's own path resolution.
        /// </summary>
        /// <remarks>
        /// <b>The one statement in the mod that turns a wire-supplied name into a
        /// path</b>, so the guard is here and not only at the caller. M9 passed a
        /// constant and needed no check; M11e carries the lobby's key, and
        /// <see cref="ScenarioPayload.IsAllowedDestination"/> is what stands between
        /// that and a <c>Path.Combine</c>. Refusing here rather than trusting the
        /// session keeps this safe on its own terms — the session checking first is
        /// defence in depth, not a substitute.
        /// </remarks>
        private static string? SaveFolder(string? saveKey)
        {
            if (!ScenarioPayload.IsAllowedDestination(saveKey))
            {
                Debug.LogWarning("[pb-and-j] refusing to resolve a save folder for '"
                    + saveKey + "' — not an allowed destination");
                return null;
            }

            var root = DataManagerSave.GetSaveFolderPath(SaveLocation.Normal);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, saveKey);
        }
    }
}
