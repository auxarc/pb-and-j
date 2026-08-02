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
