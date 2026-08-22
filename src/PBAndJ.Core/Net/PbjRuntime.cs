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
        private bool lastInCombat;
        private bool ticked;
        private double lastTickSeconds;

        public PbjRuntime(IPbjTransport transport, IPbjGameBridge bridge, IPbjLog log, PbjMailbox mailbox, IPbjSession session)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            this.session = session ?? throw new ArgumentNullException(nameof(session));

            // Seeded, not defaulted: a session started mid-combat must not
            // report entering one on its first pump.
            lastInCombat = bridge.InCombat;
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

            ObserveCombatEdge();
            ObserveTick(nowSeconds);
            ReportDrops();
        }

        /// <summary>
        /// Hands the session the time, at most four times a second.
        /// </summary>
        /// <remarks>
        /// Throttled because an unthrottled tick allocates an effect list every
        /// frame — 60 a second — to discover that nothing has expired. A quarter
        /// second is two orders of magnitude finer than the shortest interval
        /// anything here cares about.
        /// </remarks>
        private void ObserveTick(double nowSeconds)
        {
            if (ticked && nowSeconds - lastTickSeconds < PbjProtocol.TickIntervalSeconds)
            {
                return;
            }
            ticked = true;
            lastTickSeconds = nowSeconds;
            Run(session.Handle(new TickEvent(nowSeconds)));
        }

        /// <summary>
        /// Turns a change in <see cref="IPbjGameBridge.InCombat"/> into an event.
        /// </summary>
        /// <remarks>
        /// Deliberately runs *after* the drain. When the last turn's execution is
        /// what ended the combat, the queued LocalTurnComplete and the cleared
        /// InCombat flag arrive together; observing the edge first would move the
        /// host to Lobby and the final TurnComplete would be dropped by its
        /// "only while executing" guard, taking the turn's results with it.
        /// </remarks>
        private void ObserveCombatEdge()
        {
            var inCombat = bridge.InCombat;
            if (inCombat == lastInCombat)
            {
                return;
            }
            lastInCombat = inCombat;
            Run(session.Handle(inCombat
                ? (PbjInboundEvent)new CombatEnteredEvent()
                : new CombatExitedEvent()));
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
                    // Synchronous, like the commit outcome below: every order's
                    // fate is folded into the session before the commit effect
                    // behind it is dequeued, which is what lets one OrderResult
                    // describe the whole batch.
                    return session.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, result));
                }

                case CommitTurnEffect commit:
                    // The game's commit can silently refuse, so its outcome goes
                    // straight back to the session before anything is broadcast.
                    return session.Handle(new CommitOutcomeEvent(commit.Turn, bridge.CommitTurn()));

                case ApplySnapshotEffect snapshot:
                {
                    bridge.ApplySnapshot(snapshot.Units);
                    // Recomputed rather than assumed: the whole point is that the
                    // client verifies its own correction.
                    var actual = bridge.ComputeStateDigest();
                    return session.Handle(new SnapshotAppliedEvent(
                        snapshot.Turn, snapshot.Units.Count, snapshot.ExpectedDigest, actual));
                }

                case BeginLoadEffect begin:
                    // A started load reports later, through the glue's callback.
                    // One that could not start reports now — otherwise the host
                    // waits out the whole timeout on a machine that already knows
                    // the answer.
                {
                    var refusal = bridge.BeginLoad(begin.SaveKey, begin.SelectionVersion, begin.SaveDigest);
                    return refusal == null
                        ? NoEffects
                        : session.Handle(new LoadFinishedEvent(begin.SelectionVersion, refusal.Value));
                }

                case ClearLocalOrdersEffect:
                    bridge.ClearLocalOrders();
                    break;

                case PlayKeyframesEffect play:
                    // Nothing comes back, unlike the snapshot above: playback is
                    // presentation and there is no correctness claim to verify.
                    bridge.PlayKeyframes(play.Turn, play.Capture);
                    break;

                case StopKeyframesEffect:
                    bridge.StopKeyframes();
                    break;

                case BeginCombatLoadEffect beginCombat:
                    // Same effect-out, event-back shape as BeginLoad: a load that
                    // started reports through the glue's callback later, and one
                    // that could not start reports now, so the host is not left
                    // waiting out a timeout for an answer this machine already has.
                {
                    var refusal = bridge.BeginCombatLoad(beginCombat.SaveName, beginCombat.Digest);
                    return refusal == null
                        ? NoEffects
                        : session.Handle(new CombatLoadFinishedEvent(refusal.Value));
                }

                case ShipCombatEffect:
                    // Nothing comes back here either, and for a sharper reason
                    // than the mirror's: the answer is frames away. The glue
                    // polls for a moment the game will permit a save at, and says
                    // so with a LocalCombatReadyEvent when it has one — or when
                    // it has given up.
                    bridge.ShipCombat();
                    break;

                case WriteCheckpointEffect checkpoint:
                    // Nothing comes back, and unlike ShipCombat nothing reports
                    // later either. The session asks at an instant where every
                    // CanSave refusal is already known false, so a refusal is an
                    // anomaly for the glue to log once -- and no session state
                    // waits on the write, so a checkpoint that could not be taken
                    // must not stop the fight it was taken during.
                    bridge.WriteCheckpoint(checkpoint.Turn);
                    break;

                case MirrorBaseEffect mirror:
                    // Nothing comes back, for the same reason keyframes report
                    // nothing: the mirror is presentation and makes no
                    // correctness claim to verify. It is also the one effect that
                    // may land somewhere it cannot be seen — a client in a
                    // management screen gets the write without the redraw — and
                    // that is measured-correct rather than a failure to report.
                    bridge.MirrorBase(mirror.X, mirror.Z);
                    break;

                case WriteScenarioEffect write:
                    // Logged rather than fed back: nothing in the protocol waits
                    // on the write, so there is no session state to advance. The
                    // player is told to load it by hand — see the effect's note
                    // on why this does not load it for them.
                    log.Log(bridge.WriteScenario(write.Payload)
                        ? NetLog.ScenarioWritten(write.Payload.SaveName)
                        : NetLog.ScenarioWriteFailed(write.Payload.SaveName));
                    break;

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
