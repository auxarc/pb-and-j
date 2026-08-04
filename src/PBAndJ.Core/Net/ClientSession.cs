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
        private static readonly string[] NoUnits = new string[0];
        private static readonly LobbyPeerState[] NoLobbyPeers = new LobbyPeerState[0];

        private readonly IPbjGameBridge bridge;
        private readonly string playerName;
        private readonly string modVersion;

        // What we present to reclaim our units, when this session is a return
        // rather than a first arrival.
        private readonly string? resumeSessionId;
        private readonly int resumePeerId;
        private readonly string? resumeToken;

        /// <summary>
        /// Whether a Ready is outstanding for the current turn. Gates the send:
        /// un-readying without having readied is a no-op, not an unlock.
        /// </summary>
        private bool submittedThisTurn;

        // Keepalive. Stamped from the last TickEvent, the only place a clock
        // enters a session.
        private double nowSeconds;
        private double lastInboundSeconds;
        private bool ticked;
        private bool stamped;

        public ClientSession(string playerName, string modVersion, IPbjGameBridge bridge)
            : this(playerName, modVersion, bridge, null, -1)
        {
        }

        /// <summary>
        /// What this client reports about itself, and what it presents to be let
        /// in. Set before <see cref="Start"/>.
        /// </summary>
        /// <remarks>
        /// Properties rather than more constructor arguments: the constructor
        /// already carries six, and both of these are optional in the case that
        /// matters most for testing — a harness against a loopback host reports
        /// no game and needs no passphrase.
        /// </remarks>
        public string? GameBuild { get; set; }

        /// <inheritdoc cref="HelloMessage.Passphrase"/>
        public string? Passphrase { get; set; }

        /// <param name="resumeToken">
        /// The token from a previous <see cref="WelcomeMessage"/>. When present
        /// the session opens with <see cref="RejoinMessage"/> instead of
        /// <see cref="HelloMessage"/>, reclaiming the units it held before.
        /// </param>
        /// <param name="resumeSessionId">Which session the token belongs to.</param>
        /// <param name="resumePeerId">The peer id that token was issued to.</param>
        public ClientSession(
            string playerName,
            string modVersion,
            IPbjGameBridge bridge,
            string? resumeSessionId,
            int resumePeerId,
            string? resumeToken = null)
        {
            if (string.IsNullOrWhiteSpace(playerName))
            {
                throw new ArgumentException("Player name must be a non-empty string.", nameof(playerName));
            }
            this.playerName = playerName;
            this.modVersion = modVersion ?? string.Empty;
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.resumeSessionId = resumeSessionId;
            this.resumePeerId = resumePeerId;
            this.resumeToken = resumeToken;
        }

        public ClientSessionState State { get; private set; } = ClientSessionState.Handshaking;

        /// <summary>Our id in the session, or -1 before Welcome.</summary>
        public int PeerId { get; private set; } = -1;

        /// <summary>The host's turn, as last told to us.</summary>
        public int Turn { get; private set; } = -1;

        public string? SessionId { get; private set; }

        public string? HostName { get; private set; }

        /// <summary>
        /// Why the host refused us, or null if it has not.
        /// </summary>
        /// <remarks>
        /// Nullable rather than defaulting to <see cref="RejectReason.None"/>,
        /// because None is a real value on the wire and a screen reading it
        /// would announce a refusal that never happened.
        /// <para>
        /// Retained so the connect screen can name the problem. The reason was
        /// previously logged and discarded, which left the UI able to say only
        /// "failed" — and "failed" sends someone to check their firewall when
        /// the truth is that the passphrase has a typo in it.
        /// </para>
        /// </remarks>
        public RejectReason? Rejection { get; private set; }

        /// <summary>Units this client may plan, as last told by the host.</summary>
        public IReadOnlyList<string> OwnedUnits { get; private set; } = new string[0];

        /// <summary>
        /// The host's selection version, or -1 before any lobby state arrives.
        /// </summary>
        /// <remarks>
        /// -1 rather than 0 so "we have never been told" is distinguishable from
        /// "the host has not chosen yet", which is version 0. The ready guard
        /// keys off this: a client must never invent a version.
        /// </remarks>
        public int LobbySelectionVersion { get; private set; } = -1;

        /// <summary>The save the host has chosen, or null if none.</summary>
        public string? LobbySaveKey { get; private set; }

        public string? LobbySaveDigest { get; private set; }

        /// <summary>The lobby roster with ready flags, for a screen to render.</summary>
        public IReadOnlyList<LobbyPeerState> LobbyRoster { get; private set; } = NoLobbyPeers;

        /// <summary>How many lobby members have agreed to the selected save.</summary>
        /// <remarks>
        /// Derived from <see cref="LobbyRoster"/> rather than tracked separately,
        /// so a client cannot hold a count that disagrees with the roster it is
        /// drawing from. The host's <c>LobbyBarrier</c> is still the authority;
        /// this is the same answer computed from the host's own broadcast.
        /// </remarks>
        public int LobbyReadyCount
        {
            get
            {
                var ready = 0;
                for (var i = 0; i < LobbyRoster.Count; i++)
                {
                    if (LobbyRoster[i].Ready)
                    {
                        ready++;
                    }
                }
                return ready;
            }
        }

        public int LobbyParticipantCount => LobbyRoster.Count;

        /// <summary>
        /// True once every member of the last roster we were sent has agreed.
        /// </summary>
        /// <remarks>
        /// Equals the host's own <c>LobbyBarrier.IsSatisfied</c> at the moment the
        /// state was composed — the roster carries every participant and its ready
        /// flag, so there is nothing left to infer. Between broadcasts it can be
        /// stale, which is inherent to a client and not worth pretending
        /// otherwise: nothing here acts on it, and only the host may Start.
        /// </remarks>
        public bool LobbyIsSatisfied =>
            LobbyRoster.Count > 0 && LobbyReadyCount >= LobbyRoster.Count;

        /// <summary>
        /// Whether we have an outstanding lobby ready. Gates the withdrawal, the
        /// way <c>submittedThisTurn</c> gates <c>Unready</c>.
        /// </summary>
        public bool LobbyReadySent { get; private set; }

        /// <summary>
        /// The last selection version we began loading, or -1.
        /// </summary>
        /// <remarks>
        /// A load is destructive and not repeatable — it tears the campaign down
        /// — so acting on the same instruction twice must be impossible. The
        /// host's edge trigger should already make a duplicate <c>LobbyLoad</c>
        /// unreachable, but the two guards fail independently and the cost of
        /// being wrong here is the same lost campaign.
        /// </remarks>
        public int LoadBegunVersion { get; private set; } = -1;

        public IReadOnlyList<int> ConnectedPeerIds => HostOnly;

        /// <summary>
        /// The token to present if this connection drops and we come back.
        /// </summary>
        /// <remarks>
        /// Handed out by the host in Welcome. The glue must keep it somewhere
        /// that survives tearing the session down, or a reconnect has nothing to
        /// present.
        /// </remarks>
        public string? ResumeToken { get; private set; }

        /// <summary>Opens the handshake. Called once the transport connects.</summary>
        public IReadOnlyList<PbjEffect> Start()
        {
            if (resumeToken == null)
            {
                return new PbjEffect[]
                {
                    new SendEffect(HostConnectionId, new HelloMessage(
                        PbjProtocol.Magic, PbjProtocol.Version, modVersion, playerName,
                        GameBuild, Passphrase)),
                };
            }

            return new PbjEffect[]
            {
                new SendEffect(HostConnectionId, new RejoinMessage(
                    PbjProtocol.Magic, PbjProtocol.Version, modVersion, playerName,
                    resumeSessionId, resumePeerId, resumeToken, GameBuild, Passphrase)),
                new LogEffect(NetLog.Rejoining(resumeSessionId, resumePeerId)),
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

                case LocalUnreadyEvent:
                    HandleLocalUnready(effects);
                    break;

                case LocalScenarioPullEvent:
                    HandleLocalScenarioPull(effects);
                    break;

                case LocalLobbySelectEvent:
                    // The picker is the host's; ours is a display of it. Said
                    // out loud rather than swallowed, because a button that does
                    // nothing silently is the bug M10c already paid for.
                    effects.Add(new LogEffect(NetLog.LobbySelectIsHostOnly()));
                    break;

                case LocalLobbyReadyEvent:
                    HandleLocalLobbyReady(effects);
                    break;

                case LoadFinishedEvent loadFinished:
                    HandleLoadFinished(loadFinished, effects);
                    break;

                case LocalLobbyUnreadyEvent:
                    HandleLocalLobbyUnready(effects);
                    break;

                case OrderAppliedEvent:
                    // Clients never apply remote orders.
                    break;

                case SnapshotAppliedEvent applied:
                    HandleSnapshotApplied(applied, effects);
                    break;

                case CombatEnteredEvent:
                case CombatExitedEvent:
                    // A client's own combat state is not authoritative — it
                    // learns combat state from the host's CombatStart/CombatEnd.
                    // These arms exist so the edge does not throw.
                    effects.Add(new LogEffect(NetLog.CombatStateObserved(evt is CombatEnteredEvent)));
                    break;

                case TickEvent tick:
                    HandleTick(tick, effects);
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

            // Any traffic proves the host is alive.
            if (ticked)
            {
                lastInboundSeconds = nowSeconds;
                stamped = true;
            }

            if (message is PingMessage ping)
            {
                // Answered before the handshake guard below: a Ping is not a
                // protocol violation whenever it arrives, and refusing to answer
                // one would have the host reap a peer that is perfectly alive.
                effects.Add(new SendEffect(HostConnectionId, new PongMessage(ping.Nonce)));
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
                    Rejection = reject.Reason;
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

                case CombatStartMessage combatStart:
                    Turn = combatStart.Turn;
                    State = ClientSessionState.Planning;
                    submittedThisTurn = false;
                    // The lobby is over, and whatever we agreed to there is
                    // spent. Without this a client carries "ready" into the
                    // fight with nothing to clear it until the host next bumps
                    // the selection — and the host drops lobby readies during
                    // combat, so our flag and its roster would disagree.
                    LobbyReadySent = false;
                    effects.Add(new LogEffect(NetLog.CombatStartedByHost(combatStart.Turn)));
                    effects.Add(new SetExecutionLockEffect(false));
                    break;

                case CombatEndMessage:
                    State = ClientSessionState.Lobby;
                    OwnedUnits = NoUnits;
                    submittedThisTurn = false;
                    effects.Add(new LogEffect(NetLog.CombatEndedByHost()));
                    effects.Add(new StopKeyframesEffect());
                    effects.Add(new SetExecutionLockEffect(false));
                    break;

                case OrderResultMessage result:
                    effects.Add(new LogEffect(NetLog.OrderResultReceived(
                        result.Turn, result.Accepted, result.Rejected.Count)));
                    break;

                case TurnCommitMessage commit:
                    HandleTurnCommit(commit, effects);
                    break;

                case TurnCompleteMessage complete:
                    HandleTurnComplete(complete, effects);
                    break;

                case SnapshotMessage snapshot:
                    HandleSnapshot(snapshot, effects);
                    break;

                case KeyframesMessage keyframes:
                    HandleKeyframes(keyframes, effects);
                    break;

                case LobbyLoadMessage lobbyLoad:
                    HandleLobbyLoad(lobbyLoad, effects);
                    break;

                case LobbyStateMessage lobbyState:
                    HandleLobbyState(lobbyState, effects);
                    break;

                case ScenarioOfferMessage offer:
                    HandleScenarioOffer(offer, effects);
                    break;

                case ScenarioMessage scenario:
                    HandleScenario(scenario, effects);
                    break;

                case ByeMessage bye:
                    effects.Add(new LogEffect(NetLog.PeerLeft(
                        PbjPeerRegistry.HostPeerId, HostName, Describe(bye.Reason))));
                    State = ClientSessionState.Closed;
                    effects.Add(new StopKeyframesEffect());
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
            ResumeToken = welcome.ResumeToken;
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

            var captured = bridge.CaptureLocalOrders();
            var orders = OnlyOursOf(captured);

            submittedThisTurn = true;
            effects.Add(new SendEffect(HostConnectionId, new ReadyMessage(Turn, orders)));
            effects.Add(new LogEffect(NetLog.ReadyReceived(PeerId, playerName, Turn, orders.Count)));
            if (orders.Count != captured.Count)
            {
                effects.Add(new LogEffect(NetLog.OrdersNotOurs(captured.Count - orders.Count)));
            }
            effects.Add(new SetExecutionLockEffect(true));
        }

        /// <summary>
        /// Narrows a captured batch to the units this peer was dealt.
        /// </summary>
        /// <remarks>
        /// A client's local ECS holds the <em>enemy AI's</em> planned actions as
        /// well as the player's, and on a client they do not carry the
        /// <c>AIAction</c> tag the bridge filters on — whatever applies it runs at
        /// execution time, which a client never reaches. The first two-game turn
        /// therefore submitted 13 enemy orders and 3 of the host's alongside its
        /// own 2. All were correctly rejected, but they waste the wire, spend the
        /// per-message order cap and bury genuine rejections in noise.
        /// <para>
        /// This is a <b>courtesy, not an enforcement point</b>. The host checks
        /// ownership on every order regardless, and that check is what makes the
        /// rule true. So when this peer has not been told what it owns — no
        /// <c>Assignments</c> yet — it sends everything and defers, rather than
        /// silently withholding a real order on incomplete information.
        /// </para>
        /// </remarks>
        private IReadOnlyList<OrderPayload> OnlyOursOf(IReadOnlyList<OrderPayload> captured)
        {
            if (OwnedUnits.Count == 0)
            {
                return captured;
            }

            var ours = new List<OrderPayload>(captured.Count);
            for (var i = 0; i < captured.Count; i++)
            {
                var order = captured[i];
                for (var u = 0; u < OwnedUnits.Count; u++)
                {
                    // Ordinal: nameInternal is the join key every unit is
                    // addressed by, and a loose match here would turn a clean
                    // rejection into a confusing one.
                    if (string.Equals(order.OwnerName, OwnedUnits[u], StringComparison.Ordinal))
                    {
                        ours.Add(order);
                        break;
                    }
                }
            }
            return ours;
        }

        // --- lobby (M11a) ---

        /// <summary>Takes the host's view of the lobby wholesale.</summary>
        /// <remarks>
        /// Full state, so there is nothing to merge and no ordering hazard
        /// between "who joined" and "who is ready" — the same reason
        /// <c>Assignments</c> is a full replacement.
        /// </remarks>
        private void HandleLobbyState(LobbyStateMessage state, List<PbjEffect> effects)
        {
            if (state.SelectionVersion != LobbySelectionVersion)
            {
                // A new selection clears everyone's agreement on the host, so
                // ours is gone too whether we asked or not.
                LobbyReadySent = false;
            }

            LobbySelectionVersion = state.SelectionVersion;
            LobbySaveKey = state.SaveKey;
            LobbySaveDigest = state.SaveDigest;
            LobbyRoster = state.Peers;

            var readyCount = 0;
            for (var i = 0; i < state.Peers.Count; i++)
            {
                if (state.Peers[i].Ready)
                {
                    readyCount++;
                }
            }

            effects.Add(new LogEffect(NetLog.LobbyStateReceived(
                state.SelectionVersion, state.SaveKey, readyCount, state.Peers.Count)));
        }

        /// <summary>
        /// Agrees to load the selected save.
        /// </summary>
        /// <remarks>
        /// Gated on <em>data</em> — a selection we have actually been told about
        /// — and deliberately NOT on <see cref="ClientSessionState.Lobby"/>.
        /// <see cref="HandleWelcome"/> sets the state from this client's OWN
        /// <c>bridge.InCombat</c>, so a player who joins while their local game
        /// happens to be mid-combat is welcomed straight into
        /// <see cref="ClientSessionState.Planning"/>. A state guard would then
        /// refuse them the lobby forever, holding a perfectly good
        /// <c>LobbyState</c>, and no harness test would ever catch it because
        /// the scripted bridge is never in combat.
        /// <para>
        /// The host re-checks its own state regardless, so this being permissive
        /// costs nothing: a ready sent at the wrong moment is logged and dropped
        /// there rather than counted.
        /// </para>
        /// </remarks>
        /// <summary>
        /// The host says everyone agreed. Start loading.
        /// </summary>
        /// <remarks>
        /// Gated on data rather than on <see cref="State"/>, the M11a lesson:
        /// <c>HandleWelcome</c> derives that state from this client's <em>own</em>
        /// combat flag, so it is not something to make a destructive decision on.
        /// </remarks>
        private void HandleLobbyLoad(LobbyLoadMessage load, List<PbjEffect> effects)
        {
            if (load.SelectionVersion != LobbySelectionVersion)
            {
                effects.Add(new LogEffect(NetLog.LoadIgnoredStale(
                    load.SelectionVersion, LobbySelectionVersion)));
                return;
            }
            if (load.SelectionVersion == LoadBegunVersion)
            {
                effects.Add(new LogEffect(NetLog.LoadAlreadyBegun(load.SelectionVersion)));
                return;
            }

            LoadBegunVersion = load.SelectionVersion;
            effects.Add(new BeginLoadEffect(load.SaveKey, load.SelectionVersion));
        }

        private void HandleLoadFinished(LoadFinishedEvent finished, List<PbjEffect> effects)
        {
            effects.Add(new SendEffect(
                HostConnectionId,
                new LobbyLoadedMessage(finished.SelectionVersion, finished.Outcome)));
        }

        private void HandleLocalLobbyReady(List<PbjEffect> effects)
        {
            if (LobbySelectionVersion < 0)
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PeerId, LobbySelectionVersion, "no lobby state received yet")));
                return;
            }
            if (string.IsNullOrEmpty(LobbySaveKey))
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PeerId, LobbySelectionVersion, "the host has not picked a save")));
                return;
            }

            LobbyReadySent = true;
            effects.Add(new SendEffect(HostConnectionId, new LobbyReadyMessage(LobbySelectionVersion)));
            effects.Add(new LogEffect(NetLog.LobbyReadyReceived(PeerId, playerName, LobbySelectionVersion)));
        }

        private void HandleLocalLobbyUnready(List<PbjEffect> effects)
        {
            if (!LobbyReadySent)
            {
                return;
            }

            LobbyReadySent = false;
            effects.Add(new SendEffect(HostConnectionId, new LobbyUnreadyMessage(LobbySelectionVersion)));
            effects.Add(new LogEffect(NetLog.LobbyUnreadyReceived(PeerId, playerName, LobbySelectionVersion)));
        }

        /// <summary>Gives up on a host that has gone quiet.</summary>
        private void HandleTick(TickEvent tick, List<PbjEffect> effects)
        {
            nowSeconds = tick.NowSeconds;
            ticked = true;

            if (!stamped)
            {
                // Seed rather than judge on the first tick — in-game the clock
                // starts at the process uptime, so an unstamped session would
                // otherwise look silent since time zero and fault immediately.
                lastInboundSeconds = nowSeconds;
                stamped = true;
                return;
            }

            var silent = nowSeconds - lastInboundSeconds;
            if (silent >= PbjProtocol.HostTimeoutSeconds)
            {
                // Fault already unlocks execution, which is the invariant that
                // matters: a lost host must never leave the local execute button
                // disabled.
                Fault(NetLog.HostTimedOut(silent), effects);
            }
        }

        private void HandleLocalUnready(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Planning || !submittedThisTurn)
            {
                return;
            }

            submittedThisTurn = false;
            effects.Add(new SendEffect(HostConnectionId, new UnreadyMessage(Turn)));
            effects.Add(new LogEffect(NetLog.UnreadyReceived(PeerId, playerName, Turn)));
            effects.Add(new SetExecutionLockEffect(false));
        }

        private void HandleTurnCommit(TurnCommitMessage commit, List<PbjEffect> effects)
        {
            // Also the resync path: if a scenario force-execute moved the host
            // on, this is how we learn the real turn.
            Turn = commit.Turn;
            State = ClientSessionState.Watching;
            submittedThisTurn = false;
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
            submittedThisTurn = false;
            effects.Add(new SetExecutionLockEffect(false));
        }

        /// <summary>
        /// Takes the host's authoritative state: drop the stale local plan, then
        /// hard-set.
        /// </summary>
        /// <remarks>
        /// The clear comes first and is not optional. A client's planned orders
        /// are real <c>ActionEntity</c>s that never execute, so left alone they
        /// pile up and <c>CaptureLocalOrders</c> starts re-submitting orders the
        /// host already ran. The disposal cascade does not bite here because
        /// applying a snapshot writes components, not actions.
        /// </remarks>
        private void HandleSnapshot(SnapshotMessage snapshot, List<PbjEffect> effects)
        {
            effects.Add(new ClearLocalOrdersEffect());
            effects.Add(new ApplySnapshotEffect(snapshot.Turn, snapshot.Units, snapshot.Digest));
        }

        /// <summary>
        /// Starts presenting how the units reached the state we were just
        /// corrected to.
        /// </summary>
        /// <remarks>
        /// No session state changes and nothing is deferred. The snapshot that
        /// arrived immediately before this has already hard-set the authoritative
        /// state and verified its digest; playback only animates the view towards
        /// the same place, because the last key of every track is captured from
        /// the same read the snapshot is. That ordering is what lets keyframes be
        /// added without touching the correction path at all.
        /// </remarks>
        private void HandleKeyframes(KeyframesMessage keyframes, List<PbjEffect> effects)
        {
            var keyCount = 0;
            for (var i = 0; i < keyframes.Tracks.Count; i++)
            {
                keyCount += keyframes.Tracks[i].Transforms.Count;
            }

            effects.Add(new LogEffect(NetLog.KeyframesReceived(
                keyframes.Turn, keyframes.Tracks.Count, keyCount,
                keyframes.WindowStart, keyframes.WindowEnd)));
            effects.Add(new PlayKeyframesEffect(keyframes.Turn, new KeyframeCapture(
                keyframes.WindowStart, keyframes.WindowEnd, keyframes.Tracks)));
        }

        /// <summary>
        /// Decides whether the host's save is worth ~124 KB of wire.
        /// </summary>
        /// <remarks>
        /// Two conditions, both deliberately conservative, with
        /// <c>pbj.scenario-pull</c> as the override for everything they exclude:
        /// <list type="bullet">
        /// <item><b>Lobby only.</b> A client already in combat is in the host's
        /// combat; pulling a save down mid-fight is at best wasted bandwidth and
        /// at worst an invitation to load it and lose the session.</item>
        /// <item><b>Only if we do not already hold it.</b> The client reads its
        /// own save through the same bridge call the host reads its own with, so
        /// this is a local digest comparison and no bytes move. This is what makes
        /// a reconnect free: a rejoining peer holds the save by definition.</item>
        /// </list>
        /// </remarks>
        private void HandleScenarioOffer(ScenarioOfferMessage offer, List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Lobby)
            {
                return;
            }

            var local = bridge.ReadScenario();
            if (local.Inspect() == ScenarioRejection.None && local.Matches(offer.Digest))
            {
                effects.Add(new LogEffect(NetLog.ScenarioAlreadyHeld(offer.Digest)));
                return;
            }

            effects.Add(new SendEffect(HostConnectionId, new ScenarioRequestMessage(offer.Digest)));
            effects.Add(new LogEffect(NetLog.ScenarioRequested(offer.Digest)));
        }

        /// <summary>Asks for the host's save regardless of what we hold.</summary>
        private void HandleLocalScenarioPull(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Lobby && State != ClientSessionState.Planning)
            {
                return;
            }

            // No digest: this is "send me whatever you have now", which is the
            // whole point of asking by hand.
            effects.Add(new SendEffect(HostConnectionId, new ScenarioRequestMessage(null)));
            effects.Add(new LogEffect(NetLog.ScenarioRequested(null)));
        }

        /// <summary>
        /// Checks a received save and puts it on disk.
        /// </summary>
        /// <remarks>
        /// The only place in the mod where bytes off a socket become files, so
        /// nothing is taken on trust. The structural checks run first — file
        /// count, total size, allowlisted names — and the digest is recomputed
        /// from the bytes that actually arrived and compared with the one the
        /// sender claimed, so a truncated or substituted transfer is refused
        /// rather than written and then loaded.
        /// <para>
        /// Refusing is not a fault. A peer that sends a bad scenario has not
        /// broken the session, and dropping the connection over it would turn a
        /// recoverable annoyance into a lost game.
        /// </para>
        /// </remarks>
        private void HandleScenario(ScenarioMessage scenario, List<PbjEffect> effects)
        {
            var payload = new ScenarioPayload(scenario.SaveName, scenario.Files);

            var rejection = payload.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                effects.Add(new LogEffect(NetLog.ScenarioRefused(scenario.SaveName, rejection)));
                return;
            }

            if (!payload.Matches(scenario.Digest))
            {
                effects.Add(new LogEffect(NetLog.ScenarioDigestMismatch(scenario.Digest, payload.Digest)));
                return;
            }

            effects.Add(new LogEffect(NetLog.ScenarioReceived(
                scenario.SaveName, payload.Files.Count, payload.TotalBytes)));
            effects.Add(new WriteScenarioEffect(payload));
        }

        private void HandleSnapshotApplied(SnapshotAppliedEvent applied, List<PbjEffect> effects)
        {
            effects.Add(string.Equals(applied.ExpectedDigest, applied.ActualDigest, StringComparison.Ordinal)
                ? new LogEffect(NetLog.SnapshotVerified(applied.Turn, applied.UnitCount, applied.ActualDigest))
                : new LogEffect(NetLog.SnapshotStillDiverged(
                    applied.Turn, applied.ExpectedDigest, applied.ActualDigest)));
        }

        private void Fault(string line, List<PbjEffect> effects)
        {
            effects.Add(new LogEffect(line));
            State = ClientSessionState.Faulted;
            // A faulted session handles nothing further, so a playback left
            // running here would never be stopped by anything else — units would
            // slide along last turn's path into single-player.
            effects.Add(new StopKeyframesEffect());
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
