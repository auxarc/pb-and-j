using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>Where the client is in the session.</summary>
    public enum ClientSessionState
    {
        /// <summary>Socket open, Hello sent, waiting for Welcome.</summary>
        Handshaking = 0,

        /// <summary>Accepted; host is not in combat.</summary>
        Lobby = 1,

        /// <summary>In combat, planning locally.</summary>
        Planning = 2,

        /// <summary>Host is executing; local execute is locked.</summary>
        Watching = 3,

        /// <summary>Closed cleanly.</summary>
        Closed = 4,

        /// <summary>Refused or broken. Local play continues single-player.</summary>
        Faulted = 5,
    }

    /// <summary>
    /// The client half of the protocol. Sends requests, obeys the host, and
    /// never decides anything authoritative.
    /// </summary>
    public sealed class ClientSession : IPbjSession
    {
        /// <summary>
        /// A client has exactly one connection, so the transport addresses the
        /// host by this fixed id.
        /// </summary>
        public const int HostConnectionId = 0;

        private static readonly int[] HostOnly = { HostConnectionId };

        private readonly IPbjGameBridge bridge;
        private readonly string playerName;
        private readonly string modVersion;

        public ClientSession(string playerName, string modVersion, IPbjGameBridge bridge)
        {
            if (string.IsNullOrWhiteSpace(playerName))
            {
                throw new ArgumentException("Player name must be a non-empty string.", nameof(playerName));
            }
            this.playerName = playerName;
            this.modVersion = modVersion ?? string.Empty;
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public ClientSessionState State { get; private set; } = ClientSessionState.Handshaking;

        /// <summary>Our id in the session, or -1 before Welcome.</summary>
        public int PeerId { get; private set; } = -1;

        /// <summary>The host's turn, as last told to us.</summary>
        public int Turn { get; private set; } = -1;

        public string? SessionId { get; private set; }

        public string? HostName { get; private set; }

        /// <summary>Units this client may plan, as last told by the host.</summary>
        public IReadOnlyList<string> OwnedUnits { get; private set; } = new string[0];

        public IReadOnlyList<int> ConnectedPeerIds => HostOnly;

        /// <summary>Opens the handshake. Called once the transport connects.</summary>
        public IReadOnlyList<PbjEffect> Start()
        {
            return new PbjEffect[]
            {
                new SendEffect(HostConnectionId,
                    new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, modVersion, playerName)),
            };
        }

        public IReadOnlyList<PbjEffect> Handle(PbjInboundEvent evt)
        {
            if (evt == null)
            {
                throw new ArgumentNullException(nameof(evt));
            }

            var effects = new List<PbjEffect>();
            if (IsFinished)
            {
                return effects;
            }

            switch (evt)
            {
                case PeerConnectedEvent:
                    effects.AddRange(Start());
                    break;

                case PeerBytesEvent:
                    // Decoded by the runtime; never reaches the session raw.
                    break;

                case PeerDisconnectedEvent disconnected:
                    Fault(NetLog.TransportFailed(Describe(disconnected.Reason)), effects);
                    break;

                case TransportFailedEvent failed:
                    Fault(NetLog.TransportFailed(Describe(failed.Reason)), effects);
                    break;

                case TransportLogEvent log:
                    effects.Add(new LogEffect(Describe(log.Line)));
                    break;

                case LocalReadyEvent:
                    HandleLocalReady(effects);
                    break;

                case LocalTurnCompleteEvent:
                    // A client does not simulate, so its own execution-end hook
                    // carries no authority. The host's TurnComplete drives us.
                    break;

                case CommitOutcomeEvent:
                    // Clients never commit.
                    break;

                default:
                    throw new InvalidOperationException("Client session cannot handle event kind " + evt.Kind + ".");
            }

            return effects;
        }

        public IReadOnlyList<PbjEffect> HandleMessage(int peerId, PbjMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var effects = new List<PbjEffect>();
            if (IsFinished)
            {
                return effects;
            }

            if (State == ClientSessionState.Handshaking
                && !(message is WelcomeMessage)
                && !(message is RejectMessage))
            {
                Fault(NetLog.TransportFailed("host sent " + message.Type + " before Welcome"), effects);
                effects.Add(new DisconnectEffect(HostConnectionId, "protocol violation"));
                return effects;
            }

            switch (message)
            {
                case WelcomeMessage welcome:
                    HandleWelcome(welcome, effects);
                    break;

                case RejectMessage reject:
                    effects.Add(new LogEffect(NetLog.HandshakeRejected(playerName, reject.Reason, reject.Detail)));
                    State = ClientSessionState.Faulted;
                    effects.Add(new SetExecutionLockEffect(false));
                    break;

                case AssignmentsMessage assignmentsMessage:
                {
                    var mine = new List<string>();
                    for (var i = 0; i < assignmentsMessage.Assignments.Count; i++)
                    {
                        var entry = assignmentsMessage.Assignments[i];
                        if (entry.PeerId == PeerId)
                        {
                            mine.AddRange(entry.UnitNames);
                        }
                    }
                    OwnedUnits = mine;
                    effects.Add(new LogEffect(NetLog.AssignedUnits(mine)));
                    break;
                }

                case PeerJoinedMessage joined:
                    effects.Add(new LogEffect(NetLog.PeerConnected(joined.PeerId, joined.Name)));
                    break;

                case PeerLeftMessage left:
                    effects.Add(new LogEffect(NetLog.PeerLeft(left.PeerId, left.Name, "host reported")));
                    break;

                case TurnCommitMessage commit:
                    HandleTurnCommit(commit, effects);
                    break;

                case TurnCompleteMessage complete:
                    HandleTurnComplete(complete, effects);
                    break;

                case ByeMessage bye:
                    effects.Add(new LogEffect(NetLog.PeerLeft(
                        PbjPeerRegistry.HostPeerId, HostName, Describe(bye.Reason))));
                    State = ClientSessionState.Closed;
                    effects.Add(new SetExecutionLockEffect(false));
                    break;

                default:
                    // Client-only messages arriving downward.
                    Fault(NetLog.TransportFailed("host sent unexpected " + message.Type), effects);
                    effects.Add(new DisconnectEffect(HostConnectionId, "protocol violation"));
                    break;
            }

            return effects;
        }

        private void HandleWelcome(WelcomeMessage welcome, List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Handshaking)
            {
                Fault(NetLog.TransportFailed("host sent a second Welcome"), effects);
                effects.Add(new DisconnectEffect(HostConnectionId, "protocol violation"));
                return;
            }

            PeerId = welcome.AssignedPeerId;
            SessionId = welcome.SessionId;
            HostName = welcome.HostName;
            Turn = welcome.CurrentTurn;
            State = bridge.InCombat ? ClientSessionState.Planning : ClientSessionState.Lobby;

            effects.Add(new LogEffect(NetLog.Welcomed(PeerId, SessionId, HostName, Turn)));
            var roster = new List<string>();
            for (var i = 0; i < welcome.Peers.Count; i++)
            {
                roster.Add("#" + welcome.Peers[i].PeerId + " '" + Describe(welcome.Peers[i].Name) + "'");
            }
            effects.Add(new LogEffect(NetLog.SessionSummary(roster)));
        }

        private void HandleLocalReady(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Planning)
            {
                return;
            }

            var orders = bridge.CaptureLocalOrders();
            effects.Add(new SendEffect(HostConnectionId, new ReadyMessage(Turn, orders)));
            effects.Add(new LogEffect(NetLog.ReadyReceived(PeerId, playerName, Turn, orders.Count)));
            effects.Add(new SetExecutionLockEffect(true));
        }

        private void HandleTurnCommit(TurnCommitMessage commit, List<PbjEffect> effects)
        {
            // Also the resync path: if a scenario force-execute moved the host
            // on, this is how we learn the real turn.
            Turn = commit.Turn;
            State = ClientSessionState.Watching;
            effects.Add(new LogEffect(NetLog.TurnCommitted(commit.Turn)));
            effects.Add(new SetExecutionLockEffect(true));
        }

        private void HandleTurnComplete(TurnCompleteMessage complete, List<PbjEffect> effects)
        {
            var local = bridge.ComputeStateDigest();
            effects.Add(string.Equals(local, complete.Digest, StringComparison.Ordinal)
                ? new LogEffect(NetLog.DigestMatched(complete.Turn, complete.Digest))
                : new LogEffect(NetLog.DigestDiverged(complete.Turn, complete.Digest, local)));

            Turn = complete.Turn + 1;
            State = ClientSessionState.Planning;
            effects.Add(new SetExecutionLockEffect(false));
        }

        private void Fault(string line, List<PbjEffect> effects)
        {
            effects.Add(new LogEffect(line));
            State = ClientSessionState.Faulted;
            // A lost host must never leave the local execute button disabled —
            // the player continues single-player from here.
            effects.Add(new SetExecutionLockEffect(false));
        }

        private bool IsFinished =>
            State == ClientSessionState.Closed || State == ClientSessionState.Faulted;

        private static string Describe(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value!;
    }
}
