using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;

namespace PBAndJ.Peer
{
    /// <summary>A unit in the harness's stand-in world.</summary>
    [ExcludeFromCodeCoverage]
    internal sealed class ScriptedUnit
    {
        public ScriptedUnit(string name)
        {
            Name = name;
            Rotation = new Vec4(0f, 0f, 0f, 1f);
            Facing = new Vec3(0f, 0f, 1f);
            Integrity = 1f;
        }

        public string Name { get; }
        public Vec3 Position { get; set; }
        public Vec4 Rotation { get; set; }
        public Vec3 Facing { get; set; }
        public float Integrity { get; set; }

        /// <summary>
        /// The unit's live wrecked-part set. M15.
        /// </summary>
        /// <remarks>
        /// Carried by the harness for the same reason the selftest carries
        /// visibility: it is the field the snapshot leg asserts on, and a
        /// stand-in bridge that dropped it would let a codec regression through
        /// while every count still matched.
        /// </remarks>
        public IReadOnlyList<PartDestruction> WreckedParts { get; set; } =
            new PartDestruction[0];

        /// <summary>The unit's own wreck, and when. M15 section 3.1.</summary>
        public bool IsWrecked { get; set; }

        public float WreckedAt { get; set; }

        /// <summary>
        /// Every part's damage. M16.
        /// </summary>
        /// <remarks>
        /// Carried for the same reason <see cref="WreckedParts"/> is: it is what
        /// the snapshot leg asserts on, and a stand-in bridge that dropped it
        /// would let a codec regression through with every count still matching.
        /// </remarks>
        public IReadOnlyList<PartState> Parts { get; set; } = new PartState[0];

        /// <summary>
        /// Whether this unit has a frame-integrity component at all. M16.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>true</c> where the wire type defaults to <c>false</c>,
        /// deliberately: the harness's units are stand-ins for units out of
        /// combat, which is the state that <i>has</i> the component. It also means
        /// the selftest's absent case is something a leg has to set, rather than
        /// the value it would get by accident.
        /// </remarks>
        public bool HasFrameIntegrity { get; set; } = true;
    }

    /// <summary>
    /// An <see cref="IPbjGameBridge"/> backed by plain lists instead of an ECS.
    /// </summary>
    /// <remarks>
    /// Because it satisfies the same interface the game's bridge does, the
    /// harness drives the identical <see cref="PbjRuntime"/> and session code
    /// that runs inside Phantom Brigade — the protocol is exercised for real,
    /// just without a game attached.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal sealed class ScriptedGameBridge : IPbjGameBridge
    {
        private readonly List<OrderPayload> staged = new List<OrderPayload>();
        private readonly List<ScriptedUnit> units = new List<ScriptedUnit>
        {
            new ScriptedUnit("unit_a"),
            new ScriptedUnit("unit_b"),
            new ScriptedUnit("unit_c"),
        };

        public int CurrentTurn { get; set; }

        public bool InCombat { get; set; } = true;

        public IReadOnlyList<ScriptedUnit> Units => units;

        public bool ExecutionLocked { get; private set; }

        public int ClearLocalOrdersCalls { get; private set; }

        public IReadOnlyList<string> AssignableUnitNames
        {
            get
            {
                var names = new List<string>(units.Count);
                foreach (var unit in units)
                {
                    names.Add(unit.Name);
                }
                return names;
            }
        }

        public IReadOnlyList<OrderPayload> StagedOrders => staged;

        public void Stage(OrderPayload order) => staged.Add(order);

        public void ClearStaged() => staged.Clear();

        public List<OrderPayload> AppliedOrders { get; } = new List<OrderPayload>();

        public int CommitTurnCalls { get; private set; }

        /// <summary>Set false to rehearse a silently-refused commit.</summary>
        public bool CommitSucceeds { get; set; } = true;

        public IReadOnlyList<OrderPayload> CaptureLocalOrders() => staged.ToArray();

        public OrderApplyResult ApplyOrder(OrderPayload order)
        {
            AppliedOrders.Add(order);
            return OrderApplyResult.Applied;
        }

        public bool CommitTurn()
        {
            CommitTurnCalls++;
            if (CommitSucceeds)
            {
                CurrentTurn++;
            }
            return CommitSucceeds;
        }

        public void SetExecutionLocked(bool locked) => ExecutionLocked = locked;

        /// <summary>
        /// A real digest over the harness's own model.
        /// </summary>
        /// <remarks>
        /// It was a deliberate stub through M4, when the harness had no state to
        /// hash and always reporting DIVERGED was the honest answer. Since M5d it
        /// has a model, and this is what the snapshot gate compares.
        /// </remarks>
        public string ComputeStateDigest() => StateDigest.Compute(ToUnitStates());

        public IReadOnlyList<UnitSnapshot> CaptureSnapshot()
        {
            var snapshot = new List<UnitSnapshot>(units.Count);
            foreach (var unit in units)
            {
                snapshot.Add(new UnitSnapshot(
                    unit.Name, unit.Position, unit.Rotation, unit.Facing,
                    unit.Integrity,
                    isWrecked: unit.IsWrecked,
                    wreckedAt: unit.WreckedAt,
                    wreckedParts: unit.WreckedParts,
                    parts: unit.Parts,
                    hasFrameIntegrity: unit.HasFrameIntegrity));
            }
            return snapshot;
        }

        /// <summary>
        /// Replaces the model wholesale rather than merging by name.
        /// </summary>
        /// <remarks>
        /// A merge would be wrong here and would quietly fail the gate: the
        /// harness seeds itself with unit_a/b/c, and a real game sends
        /// pb_mech_01 and friends, so a name join would match nothing, leave the
        /// stand-in units in place, and report DIVERGED forever. Wholesale
        /// replacement is also the honest meaning of "hard-set" for a bridge with
        /// no world of its own.
        /// </remarks>
        public void ApplySnapshot(IReadOnlyList<UnitSnapshot> snapshot)
        {
            units.Clear();
            foreach (var incoming in snapshot)
            {
                units.Add(new ScriptedUnit(incoming.Name ?? "?")
                {
                    Position = incoming.Position,
                    Rotation = incoming.Rotation,
                    Facing = incoming.Facing,
                    Integrity = incoming.Integrity,
                    WreckedParts = incoming.WreckedParts,
                    IsWrecked = incoming.IsWrecked,
                    WreckedAt = incoming.WreckedAt,
                    Parts = incoming.Parts,
                    HasFrameIntegrity = incoming.HasFrameIntegrity,
                });
            }
        }

        public void ClearLocalOrders()
        {
            ClearLocalOrdersCalls++;
            staged.Clear();
        }

        /// <summary>
        /// What CaptureKeyframes hands back. Settable so the self-test can stand
        /// in for a real recorder.
        /// </summary>
        /// <remarks>
        /// The harness has no replay recorder and, as a client, would never
        /// capture anyway — but the host half of the self-test drives this same
        /// bridge, so it needs somewhere for a scripted turn's motion to come
        /// from.
        /// </remarks>
        public KeyframeCapture Keyframes { get; set; } = KeyframeCapture.None;

        /// <summary>The last playback started, for the self-test's assertions.</summary>
        public KeyframeCapture? Played { get; private set; }

        public int PlayedTurn { get; private set; } = -1;

        public int StopKeyframesCalls { get; private set; }

        public KeyframeCapture CaptureKeyframes() => Keyframes;

        /// <summary>
        /// Records the playback and settles the model at its end state.
        /// </summary>
        /// <remarks>
        /// A real client animates over the window; the harness has no frames to
        /// animate across, so it jumps to where the track ends. That end state is
        /// exactly what the self-test checks against the snapshot.
        /// </remarks>
        public void PlayKeyframes(int turn, KeyframeCapture capture)
        {
            Played = capture;
            PlayedTurn = turn;

            foreach (var track in capture.Tracks)
            {
                if (string.IsNullOrEmpty(track.Name)
                    || !KeyframePlayback.TrySample(track, capture.WindowEnd, out var position, out var rotation))
                {
                    continue;
                }
                foreach (var unit in units)
                {
                    if (unit.Name == track.Name)
                    {
                        unit.Position = position;
                        unit.Rotation = rotation;
                    }
                }
            }
        }

        public void StopKeyframes()
        {
            StopKeyframesCalls++;
            Played = null;
            PlayedTurn = -1;
        }

        /// <summary>
        /// Remembered and nothing else. The harness has no overworld to move a
        /// base around, and the selftest's interest is that the message crossed
        /// and was decoded, not that anything was rendered.
        /// </summary>
        public (float X, float Z)? MirroredBase { get; private set; }

        public void MirrorBase(float x, float z)
        {
            MirroredBase = (x, z);
        }

        /// <summary>
        /// The fight the harness was told to load, and what it answered.
        /// </summary>
        /// <remarks>
        /// The harness has no game to load into, so it reports success without
        /// doing anything: the selftest's interest is that the offer crossed, the
        /// bytes were fetched and the report came back, which is the whole
        /// handshake Core owns. Set <see cref="CombatLoadRefusal"/> to drive the
        /// failure arm instead.
        /// </remarks>
        public (string? SaveName, string? Digest)? CombatLoadRequested { get; private set; }

        /// <summary>What to answer, or null to report that the load started.</summary>
        public LoadOutcome? CombatLoadRefusal { get; set; }

        public LoadOutcome? BeginCombatLoad(string? saveName, string? digest)
        {
            CombatLoadRequested = (saveName, digest);
            return CombatLoadRefusal ?? LoadOutcome.Loaded;
        }

        /// <summary>
        /// Whether the session has asked for the fight to be written. M12b.
        /// </summary>
        /// <remarks>
        /// The harness has no game and no disk to write to, so it records the ask
        /// and the self-test stands in for the write. That is still worth having:
        /// what it proves is the <em>ordering</em> — that the ask arrives before
        /// anything is offered — which is the half of this the game cannot be
        /// asked about without two people and a mission.
        /// </remarks>
        public bool ShipCombatRequested { get; private set; }

        public void ShipCombat()
        {
            ShipCombatRequested = true;
        }

        /// <summary>Every turn a checkpoint was asked for, in order. M12c.</summary>
        /// <remarks>
        /// Recorded rather than no-opped so a selftest leg can assert the ORDER of
        /// the ask against the commit, which is the property M12c's moment is
        /// about. Nothing is written: the harness has no save folder and the
        /// protocol carries no byte of this.
        /// </remarks>
        public List<int> Checkpoints { get; } = new List<int>();

        public void WriteCheckpoint(int turn)
        {
            Checkpoints.Add(turn);
        }

        /// <summary>
        /// The combat save this peer "holds". In-memory rather than on disk: the
        /// harness must be runnable anywhere, and the protocol does not care
        /// where the bytes came from.
        /// </summary>
        public ScenarioPayload Scenario { get; set; } = ScenarioPayload.None;

        /// <summary>Set false to rehearse a write that fails.</summary>
        public bool ScenarioWriteSucceeds { get; set; } = true;

        /// <summary>Every scenario written, in order, for the self-test to check.</summary>
        public List<ScenarioPayload> WrittenScenarios { get; } = new List<ScenarioPayload>();

        /// <summary>
        /// Saves this peer holds under specific keys; anything else falls back to
        /// <see cref="Scenario"/> so the pre-M11e selftests keep their meaning.
        /// </summary>
        public Dictionary<string, ScenarioPayload> ScenariosByKey { get; }
            = new Dictionary<string, ScenarioPayload>(StringComparer.OrdinalIgnoreCase);

        public ScenarioPayload ReadScenario(string? saveKey)
        {
            return saveKey != null && ScenariosByKey.TryGetValue(saveKey, out var found)
                ? found
                : Scenario;
        }

        /// <summary>Save keys this bridge has been asked to load.</summary>
        /// <remarks>
        /// The harness has no game to tear down, so a load "starts" and then
        /// nothing happens — completion is posted by the scenario itself, which
        /// is exactly the shape the real glue has: begin here, report later from
        /// somewhere else entirely.
        /// </remarks>
        public List<string?> LoadsBegun { get; } = new List<string?>();

        /// <summary>Set to make the next load refuse instead of starting.</summary>
        public LoadOutcome? LoadRefusal { get; set; }

        public LoadOutcome? BeginLoad(string? saveKey, int selectionVersion, string? saveDigest)
        {
            LoadsBegun.Add(saveKey);
            return LoadRefusal;
        }

        public bool WriteScenario(ScenarioPayload payload)
        {
            if (!ScenarioWriteSucceeds)
            {
                return false;
            }
            WrittenScenarios.Add(payload);
            // A real client's next ReadScenario would find what it just wrote, so
            // the harness's must too — that equality is what makes a second offer
            // decline rather than transfer all over again.
            Scenario = payload;
            return true;
        }

        private UnitState[] ToUnitStates()
        {
            var states = new UnitState[units.Count];
            for (var i = 0; i < units.Count; i++)
            {
                states[i] = new UnitState(units[i].Name, units[i].Position, units[i].Integrity);
            }
            return states;
        }
    }

    [ExcludeFromCodeCoverage]
    internal sealed class ConsoleLog : IPbjLog
    {
        public void Log(string line) => Console.WriteLine(line);
    }
}
