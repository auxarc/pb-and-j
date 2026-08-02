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
    [ExcludeFromCodeCoverage]
    internal sealed class ScriptedGameBridge : IPbjGameBridge
    {
        private readonly List<OrderPayload> staged = new List<OrderPayload>();

        public int CurrentTurn { get; set; }

        public bool InCombat { get; set; } = true;

        public List<string> Units { get; } = new List<string> { "unit_a", "unit_b", "unit_c" };

        public bool ExecutionLocked { get; private set; }

        public IReadOnlyList<string> AssignableUnitNames => Units;

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
        /// Deliberately not a real state hash — a harness has no units, so it
        /// will diverge from the host, which is exactly what the divergence
        /// detector should report.
        /// </summary>
        public string ComputeStateDigest() => StateDigest.Compute(Array.Empty<UnitState>());
    }

    [ExcludeFromCodeCoverage]
    internal sealed class ConsoleLog : IPbjLog
    {
        public void Log(string line) => Console.WriteLine(line);
    }
}
