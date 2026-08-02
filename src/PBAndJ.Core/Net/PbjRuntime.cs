using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Drains the mailbox, decodes frames, feeds the session, and carries out
    /// the effects it returns. The whole main-thread half of networking.
    /// </summary>
    /// <remarks>
    /// Lives in Core rather than the glue so the effect runner is covered by the
    /// gate and shared verbatim by the game and the harness — the harness
    /// self-test therefore exercises the same code path the game runs.
    /// <para>
    /// Pumped from a Harmony postfix on <c>Heartbeat.Update</c>, on the main
    /// thread, always. Nothing here is thread-safe except the mailbox itself.
    /// </para>
    /// </remarks>
    public sealed class PbjRuntime
    {
        /// <summary>Largest frame accepted from a peer.</summary>
        public const int MaxFrameLength = 1 << 20;

        private readonly IPbjTransport transport;
        private readonly IPbjGameBridge bridge;
        private readonly IPbjLog log;
        private readonly PbjMailbox mailbox;
        private readonly IPbjSession session;
        private readonly Dictionary<int, FrameDecoder> decoders = new Dictionary<int, FrameDecoder>();

        private int reportedDrops;
        private bool stopped;

        public PbjRuntime(IPbjTransport transport, IPbjGameBridge bridge, IPbjLog log, PbjMailbox mailbox, IPbjSession session)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public IPbjSession Session => session;

        /// <summary>
        /// Processes everything queued since the last call. Time is passed in
        /// rather than read, so Core never touches a clock and timeout logic
        /// stays a deterministic unit test.
        /// </summary>
        public void Pump(double nowSeconds)
        {
            if (stopped)
            {
                return;
            }

            foreach (var evt in mailbox.DrainAll())
            {
                if (evt is PeerBytesEvent bytes)
                {
                    HandleBytes(bytes);
                    continue;
                }

                if (evt is PeerDisconnectedEvent disconnected)
                {
                    // Drop the partial frame state; a reconnecting peer on the
                    // same id must not inherit half a message.
                    decoders.Remove(disconnected.PeerId);
                }

                Run(session.Handle(evt));
            }

            ReportDrops();
        }

        /// <summary>Feeds a locally-originated event, for console commands.</summary>
        public void Post(PbjInboundEvent evt)
        {
            mailbox.Post(evt);
        }

        public void Stop()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            transport.Stop();
            mailbox.DrainAll();
            decoders.Clear();
        }

        private void HandleBytes(PeerBytesEvent evt)
        {
            if (!decoders.TryGetValue(evt.PeerId, out var decoder))
            {
                decoder = new FrameDecoder(MaxFrameLength);
                decoders[evt.PeerId] = decoder;
            }

            IReadOnlyList<byte[]> frames;
            try
            {
                frames = decoder.Feed(evt.Bytes, 0, evt.Bytes.Length);
            }
            catch (PbjProtocolException e)
            {
                DropPeer(evt.PeerId, "malformed frame: " + e.Message);
                return;
            }

            for (var i = 0; i < frames.Count; i++)
            {
                PbjMessage message;
                try
                {
                    message = PbjMessageCodec.Decode(frames[i]);
                }
                catch (PbjProtocolException e)
                {
                    DropPeer(evt.PeerId, "undecodable message: " + e.Message);
                    return;
                }
                Run(session.HandleMessage(evt.PeerId, message));
            }
        }

        private void DropPeer(int peerId, string reason)
        {
            log.Log(NetLog.PeerLeft(peerId, null, reason));
            decoders.Remove(peerId);
            transport.Disconnect(peerId, reason);
            Run(session.Handle(new PeerDisconnectedEvent(peerId, reason)));
        }

        private void Run(IReadOnlyList<PbjEffect> effects)
        {
            // A queue, not a foreach: CommitTurnEffect feeds its outcome straight
            // back into the session, which produces more effects in the same pump.
            var pending = new Queue<PbjEffect>();
            for (var i = 0; i < effects.Count; i++)
            {
                pending.Enqueue(effects[i]);
            }

            while (pending.Count > 0)
            {
                foreach (var produced in Execute(pending.Dequeue()))
                {
                    pending.Enqueue(produced);
                }
            }
        }

        private IReadOnlyList<PbjEffect> Execute(PbjEffect effect)
        {
            switch (effect)
            {
                case SendEffect send:
                    SendTo(send.PeerId, send.Message);
                    break;

                case BroadcastEffect broadcast:
                {
                    var peers = session.ConnectedPeerIds;
                    for (var i = 0; i < peers.Count; i++)
                    {
                        if (broadcast.ExceptPeerId.HasValue && peers[i] == broadcast.ExceptPeerId.Value)
                        {
                            continue;
                        }
                        SendTo(peers[i], broadcast.Message);
                    }
                    break;
                }

                case DisconnectEffect disconnect:
                    decoders.Remove(disconnect.PeerId);
                    transport.Disconnect(disconnect.PeerId, disconnect.Reason);
                    break;

                case ApplyOrderEffect apply:
                {
                    var result = bridge.ApplyOrder(apply.Order);
                    if (result != OrderApplyResult.Applied)
                    {
                        log.Log(NetLog.OrderRejectedByGame(
                            apply.PeerId, apply.Order.OwnerName, apply.Order.Blueprint, result));
                    }
                    break;
                }

                case CommitTurnEffect commit:
                    // The game's commit can silently refuse, so its outcome goes
                    // straight back to the session before anything is broadcast.
                    return session.Handle(new CommitOutcomeEvent(commit.Turn, bridge.CommitTurn()));

                case SetExecutionLockEffect setLock:
                    bridge.SetExecutionLocked(setLock.Locked);
                    break;

                case LogEffect logEffect:
                    log.Log(logEffect.Line);
                    break;

                default:
                    throw new InvalidOperationException("No runner for effect kind " + effect.Kind + ".");
            }

            return NoEffects;
        }

        private void SendTo(int peerId, PbjMessage message)
        {
            // Encoding is deliberately not guarded: PbjMessageCodec only refuses
            // a message type it has no case for, which means we forgot to add
            // one. That should fail loudly in tests rather than be swallowed at
            // runtime — the pump's own try/catch stops it bricking the game.
            var frame = FrameEncoder.Encode(PbjMessageCodec.Encode(message));

            try
            {
                transport.Send(peerId, frame);
            }
            catch (Exception e)
            {
                // One peer's dead socket must not take down the session.
                log.Log(NetLog.PeerLeft(peerId, null, "send failed: " + e.Message));
                decoders.Remove(peerId);
                transport.Disconnect(peerId, "send failed");
            }
        }

        private void ReportDrops()
        {
            var dropped = mailbox.DroppedCount;
            if (dropped > reportedDrops)
            {
                log.Log(NetLog.MailboxOverflowed(dropped - reportedDrops));
                reportedDrops = dropped;
            }
        }

        private static readonly PbjEffect[] NoEffects = new PbjEffect[0];
    }
}
