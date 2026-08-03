using System;
using System.Collections.Generic;
using PBAndJ.Core.Net;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>In-memory stand-in for the game. No ECS, no Unity, no timing.</summary>
    internal sealed class FakeGameBridge : IPbjGameBridge
    {
        public int CurrentTurn { get; set; } = 3;
        public bool InCombat { get; set; } = true;

        public List<string> Assignable { get; } = new List<string> { "unit_a", "unit_b", "unit_c" };
        public List<OrderPayload> LocalOrders { get; } = new List<OrderPayload>();

        /// <summary>What ApplyOrder should return, keyed by owner name.</summary>
        public Dictionary<string, OrderApplyResult> ApplyResults { get; } =
            new Dictionary<string, OrderApplyResult>();

        public List<OrderPayload> Applied { get; } = new List<OrderPayload>();
        public List<bool> LockCalls { get; } = new List<bool>();
        public int CommitCalls { get; private set; }

        /// <summary>Simulates ConfirmExecution silently refusing.</summary>
        public bool CommitSucceeds { get; set; } = true;

        public string Digest { get; set; } = "deadbeef";

        IReadOnlyList<string> IPbjGameBridge.AssignableUnitNames => Assignable;

        public IReadOnlyList<OrderPayload> CaptureLocalOrders() => LocalOrders;

        public OrderApplyResult ApplyOrder(OrderPayload order)
        {
            Applied.Add(order);
            return ApplyResults.TryGetValue(order.OwnerName, out var result) ? result : OrderApplyResult.Applied;
        }

        public bool CommitTurn()
        {
            CommitCalls++;
            if (CommitSucceeds)
            {
                CurrentTurn++;
            }
            return CommitSucceeds;
        }

        public void SetExecutionLocked(bool locked) => LockCalls.Add(locked);

        public string ComputeStateDigest() => Digest;

        /// <summary>What CaptureSnapshot hands back.</summary>
        public List<UnitSnapshot> Snapshot { get; } = new List<UnitSnapshot>();

        /// <summary>Every snapshot handed to ApplySnapshot, in order.</summary>
        public List<IReadOnlyList<UnitSnapshot>> AppliedSnapshots { get; } =
            new List<IReadOnlyList<UnitSnapshot>>();

        public int ClearLocalOrdersCalls { get; private set; }

        /// <summary>
        /// Digest to report once a snapshot has been applied — lets a test make
        /// the correction land or deliberately fail to.
        /// </summary>
        public string? DigestAfterApply { get; set; }

        public IReadOnlyList<UnitSnapshot> CaptureSnapshot() => Snapshot;

        public void ApplySnapshot(IReadOnlyList<UnitSnapshot> units)
        {
            AppliedSnapshots.Add(units);
            if (DigestAfterApply != null)
            {
                Digest = DigestAfterApply;
            }
        }

        public void ClearLocalOrders() => ClearLocalOrdersCalls++;

        /// <summary>What CaptureKeyframes hands back.</summary>
        public KeyframeCapture Keyframes { get; set; } = KeyframeCapture.None;

        /// <summary>Every playback started, in order.</summary>
        public List<(int Turn, KeyframeCapture Capture)> Played { get; } =
            new List<(int, KeyframeCapture)>();

        public int StopKeyframesCalls { get; private set; }

        public KeyframeCapture CaptureKeyframes() => Keyframes;

        public void PlayKeyframes(int turn, KeyframeCapture capture) => Played.Add((turn, capture));

        public void StopKeyframes() => StopKeyframesCalls++;

        /// <summary>The local combat save, as ReadScenario hands it back.</summary>
        public ScenarioPayload Scenario { get; set; } = ScenarioPayload.None;

        /// <summary>Every scenario written to disk, in order.</summary>
        public List<ScenarioPayload> WrittenScenarios { get; } = new List<ScenarioPayload>();

        /// <summary>Simulates the write failing — no space, no permission.</summary>
        public bool ScenarioWriteSucceeds { get; set; } = true;

        public ScenarioPayload ReadScenario() => Scenario;

        public bool WriteScenario(ScenarioPayload payload)
        {
            WrittenScenarios.Add(payload);
            return ScenarioWriteSucceeds;
        }
    }

    /// <summary>Records everything sent, without a socket.</summary>
    internal sealed class FakeTransport : IPbjTransport
    {
        public List<(int PeerId, byte[] Frame)> Sent { get; } = new List<(int, byte[])>();
        public List<(int PeerId, string? Reason)> Disconnected { get; } = new List<(int, string?)>();
        public int StopCalls { get; private set; }

        public void Send(int peerId, byte[] frame) => Sent.Add((peerId, frame));

        public void Disconnect(int peerId, string? reason) => Disconnected.Add((peerId, reason));

        public void Stop() => StopCalls++;

        /// <summary>Decodes what was sent to a peer, for assertions.</summary>
        public List<PbjMessage> MessagesTo(int peerId)
        {
            var messages = new List<PbjMessage>();
            var decoder = new FrameDecoder(1 << 20);
            foreach (var (id, frame) in Sent)
            {
                if (id != peerId)
                {
                    continue;
                }
                foreach (var payload in decoder.Feed(frame, 0, frame.Length))
                {
                    messages.Add(PbjMessageCodec.Decode(payload));
                }
            }
            return messages;
        }
    }

    /// <summary>Fails on every send, to cover the write-failure path.</summary>
    internal sealed class ThrowingTransport : IPbjTransport
    {
        public List<(int PeerId, string? Reason)> Disconnected { get; } = new List<(int, string?)>();
        public int StopCalls { get; private set; }

        public void Send(int peerId, byte[] frame) => throw new InvalidOperationException("socket is gone");

        public void Disconnect(int peerId, string? reason) => Disconnected.Add((peerId, reason));

        public void Stop() => StopCalls++;
    }

    internal sealed class RecordingLog : IPbjLog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Log(string line) => Lines.Add(line);

        public bool Contains(string fragment) => Lines.Exists(l => l.Contains(fragment));
    }
}
