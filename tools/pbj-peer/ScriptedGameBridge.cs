using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// An <see cref="IPbjGameBridge"/> backed by plain lists instead of an ECS.
    /// </summary>
    /// <remarks>
    /// Because it satisfies the same interface the game's bridge does, the
    /// harness drives the identical <see cref="PbjRuntime"/> and session code
    /// that runs inside Phantom Brigade — the protocol is exercised for real,
    /// just without a game attached.
    /// </remarks>
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
        public bool IsDead { get; set; }
        public float DeathTime { get; set; }
    }

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
                    unit.Integrity, unit.IsDead, unit.DeathTime));
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
                    IsDead = incoming.IsDead,
                    DeathTime = incoming.DeathTime,
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
