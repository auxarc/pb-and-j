using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>Where the host is in the turn cycle.</summary>
    public enum HostSessionState
    {
        /// <summary>Accepting peers; not in combat.</summary>
        Lobby = 0,

        /// <summary>In combat, collecting orders.</summary>
        Planning = 1,

        /// <summary>Commit issued, waiting for the simulation to finish.</summary>
        Executing = 2,

        /// <summary>Torn down.</summary>
        Closed = 3,
    }

    /// <summary>
    /// The host half of the protocol: a pure
    /// <c>(state, event) -&gt; (state, effect[])</c> machine.
    /// </summary>
    /// <remarks>
    /// Holds no socket, no ECS and no clock. It reads the game only through
    /// <see cref="IPbjGameBridge"/> queries and expresses every side effect as a
    /// <see cref="PbjEffect"/>, which is what lets the whole protocol be tested
    /// with fakes and no timing.
    /// </remarks>
    public sealed class HostSession : IPbjSession
    {
        private readonly IPbjGameBridge bridge;
        private readonly PbjPeerRegistry registry;
        private readonly TurnBarrier barrier;
        private readonly Dictionary<int, List<OrderPayload>> submitted = new Dictionary<int, List<OrderPayload>>();

        private UnitAssignments assignments = UnitAssignments.Empty;
        private int committedTurn = -1;

        public HostSession(string hostName, string sessionId, int maxPeers, IPbjGameBridge bridge)
        {
            if (string.IsNullOrWhiteSpace(hostName))
            {
                throw new ArgumentException("Host name must be a non-empty string.", nameof(hostName));
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session id must be a non-empty string.", nameof(sessionId));
            }
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

            HostName = hostName;
            SessionId = sessionId;
            registry = new PbjPeerRegistry(maxPeers);
            barrier = new TurnBarrier(bridge.CurrentTurn);
            barrier.AddParticipant(PbjPeerRegistry.HostPeerId);
            State = bridge.InCombat ? HostSessionState.Planning : HostSessionState.Lobby;
        }

        public string HostName { get; }
        public string SessionId { get; }
        public HostSessionState State { get; private set; }
        public int Turn => barrier.Turn;
        public int ParticipantCount => barrier.ParticipantCount;
        public int ReadyCount => barrier.ReadyCount;
        public UnitAssignments Assignments => assignments;
        public IReadOnlyList<PbjPeer> Peers => registry.Peers;

        public IReadOnlyList<int> ConnectedPeerIds
        {
            get
            {
                var ids = new List<int>();
                foreach (var peer in registry.Peers)
                {
                    ids.Add(peer.PeerId);
                }
                return ids;
            }
        }

        public IReadOnlyList<PbjEffect> Handle(PbjInboundEvent evt)
        {
            if (evt == null)
            {
                throw new ArgumentNullException(nameof(evt));
            }

            var effects = new List<PbjEffect>();
            if (State == HostSessionState.Closed)
            {
                return effects;
            }

            switch (evt)
            {
                case PeerConnectedEvent connected:
                    // Nothing happens until Hello — an accepted socket is not yet a peer.
                    effects.Add(new LogEffect(NetLog.PeerConnected(connected.PeerId, connected.Remote)));
                    break;

                case PeerBytesEvent:
                    // Decoded by the runtime; never reaches the session raw.
                    break;

                case PeerDisconnectedEvent disconnected:
                    HandleDisconnect(disconnected.PeerId, disconnected.Reason, effects);
                    break;

                case TransportFailedEvent failed:
                    effects.Add(new LogEffect(NetLog.TransportFailed(Describe(failed.Reason))));
                    effects.Add(new SetExecutionLockEffect(false));
                    State = HostSessionState.Closed;
                    break;

                case TransportLogEvent log:
                    effects.Add(new LogEffect(Describe(log.Line)));
                    break;

                case LocalReadyEvent:
                    HandleLocalReady(effects);
                    break;

                case CommitOutcomeEvent outcome:
                    HandleCommitOutcome(outcome, effects);
                    break;

                case LocalTurnCompleteEvent complete:
                    HandleLocalTurnComplete(complete, effects);
                    break;

                default:
                    throw new InvalidOperationException("Host session cannot handle event kind " + evt.Kind + ".");
            }

            return effects;
        }

        /// <summary>Handles a decoded message from a peer. Called by the runtime.</summary>
        public IReadOnlyList<PbjEffect> HandleMessage(int peerId, PbjMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var effects = new List<PbjEffect>();
            if (State == HostSessionState.Closed)
            {
                return effects;
            }

            switch (message)
            {
                case HelloMessage hello:
                    HandleHello(peerId, hello, effects);
                    break;

                case ReadyMessage ready:
                    HandleReady(peerId, ready, effects);
                    break;

                case ByeMessage bye:
                    HandleDisconnect(peerId, Describe(bye.Reason), effects);
                    effects.Add(new DisconnectEffect(peerId, "bye"));
                    break;

                default:
                    // Host-only messages arriving upward, or anything unexpected.
                    effects.Add(new LogEffect(NetLog.PeerLeft(peerId, NameOf(peerId), "protocol violation: " + message.Type)));
                    effects.Add(new DisconnectEffect(peerId, "protocol violation"));
                    RemovePeer(peerId);
                    break;
            }

            return effects;
        }

        private void HandleHello(int peerId, HelloMessage hello, List<PbjEffect> effects)
        {
            if (registry.TryGet(peerId, out _))
            {
                effects.Add(new LogEffect(NetLog.PeerLeft(peerId, NameOf(peerId), "duplicate hello")));
                effects.Add(new DisconnectEffect(peerId, "duplicate hello"));
                RemovePeer(peerId);
                return;
            }

            var protocolFault = PbjProtocol.Check(hello.Magic, hello.ProtocolVersion);
            if (protocolFault != null)
            {
                var detail = protocolFault == RejectReason.VersionMismatch
                    ? "peer v" + hello.ProtocolVersion + ", host v" + PbjProtocol.Version
                    : null;
                Reject(peerId, hello.PlayerName, protocolFault.Value, detail, effects);
                return;
            }

            var refusal = registry.Add(peerId, hello.PlayerName, out var peer);
            if (refusal != null)
            {
                Reject(peerId, hello.PlayerName, refusal.Value, null, effects);
                return;
            }

            barrier.AddParticipant(peerId);

            effects.Add(new SendEffect(peerId, new WelcomeMessage(
                PbjProtocol.Version, SessionId, peerId, HostName, RosterIncludingHost(), barrier.Turn)));
            effects.Add(new LogEffect(NetLog.HandshakeOk(
                peerId, peer!.Name, hello.ProtocolVersion, hello.ModVersion)));
            effects.Add(new BroadcastEffect(new PeerJoinedMessage(peerId, peer.Name), peerId));
            effects.Add(new LogEffect(NetLog.SessionSummary(ParticipantDescriptions())));

            Reassign(effects);
        }

        private void Reject(int peerId, string? name, RejectReason reason, string? detail, List<PbjEffect> effects)
        {
            effects.Add(new SendEffect(peerId, new RejectMessage(reason, detail)));
            effects.Add(new LogEffect(NetLog.HandshakeRejected(name, reason, detail)));
            effects.Add(new DisconnectEffect(peerId, reason.ToString()));
        }

        private void HandleReady(int peerId, ReadyMessage ready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "ready before hello"));
                RemovePeer(peerId);
                return;
            }
            if (State == HostSessionState.Executing)
            {
                effects.Add(new LogEffect(NetLog.ReadyIgnoredStale(peerId, ready.Turn, barrier.Turn)));
                return;
            }

            // Not a switch: ReadyOutcome.UnknownParticipant cannot occur here,
            // because a registered peer is always a barrier participant and
            // registration was verified above. A case for it would be dead code.
            var outcome = barrier.SetReady(peerId, ready.Turn);
            if (outcome == ReadyOutcome.Stale)
            {
                effects.Add(new LogEffect(NetLog.ReadyIgnoredStale(peerId, ready.Turn, barrier.Turn)));
                return;
            }
            if (outcome == ReadyOutcome.NeedsResync)
            {
                // A scenario force-execute can advance the host's turn outside
                // the barrier, so being ahead is not the peer's fault and must
                // not disconnect it.
                effects.Add(new LogEffect(NetLog.ReadyNeedsResync(peerId, ready.Turn, barrier.Turn)));
                effects.Add(new SendEffect(peerId, new TurnCommitMessage(barrier.Turn)));
                return;
            }

            submitted[peerId] = new List<OrderPayload>(ready.Orders);
            effects.Add(new LogEffect(NetLog.ReadyReceived(peerId, NameOf(peerId), ready.Turn, ready.Orders.Count)));
            TryCommit(effects);
        }

        private void HandleLocalReady(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning)
            {
                return;
            }
            barrier.SetReady(PbjPeerRegistry.HostPeerId, barrier.Turn);
            TryCommit(effects);
        }

        private void TryCommit(List<PbjEffect> effects)
        {
            if (!barrier.IsSatisfied)
            {
                effects.Add(new LogEffect(NetLog.BarrierWaiting(barrier.ReadyCount, barrier.ParticipantCount)));
                return;
            }

            effects.Add(new LogEffect(NetLog.BarrierCommitting(
                barrier.ReadyCount, barrier.ParticipantCount, barrier.Turn)));

            // Apply every remote order, then commit, then VERIFY, then broadcast.
            // Broadcasting first would leave peers locked forever whenever the
            // game silently refuses the commit.
            var applied = 0;
            foreach (var peerId in SubmittingPeers())
            {
                var orders = submitted[peerId];
                for (var i = 0; i < orders.Count; i++)
                {
                    var order = orders[i];
                    if (!assignments.IsOwnedBy(peerId, order.OwnerName))
                    {
                        effects.Add(new LogEffect(NetLog.OrderRejectedUnowned(peerId, order.OwnerName)));
                        continue;
                    }
                    effects.Add(new ApplyOrderEffect(peerId, order));
                    applied++;
                }
            }

            effects.Add(new LogEffect(NetLog.OrdersApplied(applied, 0)));
            committedTurn = barrier.Turn;
            effects.Add(new CommitTurnEffect(committedTurn));
        }

        private void HandleCommitOutcome(CommitOutcomeEvent outcome, List<PbjEffect> effects)
        {
            if (!outcome.Committed)
            {
                effects.Add(new LogEffect(NetLog.CommitRefused(outcome.Turn)));
                barrier.Unready(PbjPeerRegistry.HostPeerId);
                foreach (var peer in registry.Peers)
                {
                    barrier.Unready(peer.PeerId);
                }
                submitted.Clear();
                effects.Add(new SetExecutionLockEffect(false));
                return;
            }

            State = HostSessionState.Executing;
            submitted.Clear();
            effects.Add(new LogEffect(NetLog.TurnCommitted(outcome.Turn)));
            effects.Add(new BroadcastEffect(new TurnCommitMessage(outcome.Turn)));
            effects.Add(new SetExecutionLockEffect(true));
        }

        private void HandleLocalTurnComplete(LocalTurnCompleteEvent complete, List<PbjEffect> effects)
        {
            if (State != HostSessionState.Executing)
            {
                return;
            }

            // The ECS has already advanced past the executed turn, so report the
            // number captured at commit time rather than reading it back.
            effects.Add(new LogEffect(NetLog.TurnCompleted(committedTurn, complete.Digest, registry.Count)));
            effects.Add(new BroadcastEffect(new TurnCompleteMessage(committedTurn, complete.Digest)));

            barrier.AdvanceTo(bridge.CurrentTurn);
            State = HostSessionState.Planning;
            effects.Add(new SetExecutionLockEffect(false));
        }

        private void HandleDisconnect(int peerId, string? reason, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out var peer))
            {
                return;
            }

            effects.Add(new LogEffect(NetLog.PeerLeft(peerId, peer!.Name, reason)));
            RemovePeer(peerId);
            effects.Add(new BroadcastEffect(new PeerLeftMessage(peerId, peer.Name)));
            Reassign(effects);

            // Removing a peer can satisfy the barrier — a departing peer must
            // never wedge the session.
            if (State == HostSessionState.Planning)
            {
                TryCommit(effects);
            }
        }

        private void RemovePeer(int peerId)
        {
            registry.Remove(peerId, out _);
            barrier.RemoveParticipant(peerId);
            submitted.Remove(peerId);
        }

        private void Reassign(List<PbjEffect> effects)
        {
            if (!bridge.InCombat)
            {
                return;
            }
            assignments = UnitAssignmentPlanner.Plan(ParticipantIds(), bridge.AssignableUnitNames);
            effects.Add(new LogEffect(NetLog.Assignment(assignments)));

            // Clients cannot plan without knowing what they own. Advisory only —
            // every inbound order is still re-checked against our own copy.
            var entries = new List<PeerAssignment>();
            foreach (var peerId in assignments.PeerIds)
            {
                entries.Add(new PeerAssignment(peerId, assignments.UnitsFor(peerId)));
            }
            effects.Add(new BroadcastEffect(new AssignmentsMessage(entries)));
        }

        private List<int> ParticipantIds()
        {
            var ids = new List<int> { PbjPeerRegistry.HostPeerId };
            foreach (var peer in registry.Peers)
            {
                ids.Add(peer.PeerId);
            }
            return ids;
        }

        private List<int> SubmittingPeers()
        {
            var ids = new List<int>();
            foreach (var peer in registry.Peers)
            {
                if (submitted.ContainsKey(peer.PeerId))
                {
                    ids.Add(peer.PeerId);
                }
            }
            return ids;
        }

        private PeerInfo[] RosterIncludingHost()
        {
            var roster = new PeerInfo[registry.Count + 1];
            roster[0] = new PeerInfo(PbjPeerRegistry.HostPeerId, HostName);
            for (var i = 0; i < registry.Peers.Count; i++)
            {
                roster[i + 1] = new PeerInfo(registry.Peers[i].PeerId, registry.Peers[i].Name);
            }
            return roster;
        }

        private List<string> ParticipantDescriptions()
        {
            var descriptions = new List<string> { "host #0 '" + HostName + "'" };
            foreach (var peer in registry.Peers)
            {
                descriptions.Add("#" + peer.PeerId + " '" + peer.Name + "'");
            }
            return descriptions;
        }

        private string? NameOf(int peerId)
        {
            return registry.TryGet(peerId, out var peer) ? peer!.Name : null;
        }

        private static string Describe(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value!;
        }
    }
}
