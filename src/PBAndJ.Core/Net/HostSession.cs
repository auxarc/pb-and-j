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

        /// <summary>
        /// Who has agreed to load the selected save. Separate from
        /// <see cref="barrier"/> on purpose — see <see cref="LobbyBarrier"/>.
        /// </summary>
        private readonly LobbyBarrier lobby = new LobbyBarrier(LobbySelection.None.Version);
        private LobbySelection selection = LobbySelection.None;
        private readonly Dictionary<int, List<OrderPayload>> submitted = new Dictionary<int, List<OrderPayload>>();

        // Per-peer outcome of the batch currently being committed. Populated by
        // TryCommit (ownership) and OrderAppliedEvent (the game's verdict),
        // flushed as OrderResult once the commit is known to have landed.
        private readonly Dictionary<int, int> pendingAccepted = new Dictionary<int, int>();
        private readonly Dictionary<int, List<RejectedOrder>> pendingRejections =
            new Dictionary<int, List<RejectedOrder>>();
        private readonly List<int> pendingResultOrder = new List<int>();

        // Keepalive. Stamped with the time carried by the last TickEvent, since
        // that is the only place a clock enters a session — at most one tick
        // interval stale against a 20s timeout.
        private readonly Dictionary<int, double> lastInboundSeconds = new Dictionary<int, double>();
        private readonly Dictionary<int, double> lastPingSeconds = new Dictionary<int, double>();

        /// <summary>
        /// Peers that dropped and may still come back, keyed by the peer id they
        /// held. Keyed by id rather than token because a 32-bit token can collide
        /// between two departures; the token is compared separately.
        /// </summary>
        private readonly Dictionary<int, DepartedPeer> departed = new Dictionary<int, DepartedPeer>();

        private readonly string sessionSecret;
        private readonly SessionRequirements requirements;

        /// <summary>
        /// Sockets that have connected but not yet handshook, and when they did.
        /// </summary>
        /// <remarks>
        /// An accepted socket is not a peer and is not in the registry, so
        /// nothing else was tracking these. On a loopback listener that is
        /// harmless; on an internet-facing one, anything that connects and stays
        /// mute would sit here forever and cost a connection slot for free.
        /// </remarks>
        private readonly Dictionary<int, double> pendingHandshakes = new Dictionary<int, double>();

        private UnitAssignments assignments = UnitAssignments.Empty;
        private int committedTurn = -1;
        private double nowSeconds;
        private bool ticked;
        private int nextPingNonce;

        /// <summary>A player whose units are being held for its return.</summary>
        private sealed class DepartedPeer
        {
            public DepartedPeer(int peerId, string name, string token, double departedAtSeconds)
            {
                PeerId = peerId;
                Name = name;
                Token = token;
                DepartedAtSeconds = departedAtSeconds;
            }

            public int PeerId { get; }
            public string Name { get; }
            public string Token { get; }
            public double DepartedAtSeconds { get; }
        }

        /// <param name="sessionSecret">
        /// Minted by the glue, never sent, and used only to derive resume tokens.
        /// It exists because a token derived from anything the wire already
        /// carries — session id, peer id, player name — is no secret at all:
        /// every one of those reaches every client, so any peer could compute a
        /// departed player's token and steal its units. Deriving from a secret
        /// keeps the session a deterministic pure machine (tests pass a fixed
        /// one) without needing a randomness seam.
        /// </param>
        public HostSession(
            string hostName,
            string sessionId,
            int maxPeers,
            IPbjGameBridge bridge,
            string sessionSecret,
            SessionRequirements requirements)
        {
            if (string.IsNullOrWhiteSpace(hostName))
            {
                throw new ArgumentException("Host name must be a non-empty string.", nameof(hostName));
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session id must be a non-empty string.", nameof(sessionId));
            }
            if (string.IsNullOrWhiteSpace(sessionSecret))
            {
                throw new ArgumentException("Session secret must be a non-empty string.", nameof(sessionSecret));
            }
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            this.sessionSecret = sessionSecret;
            this.requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));

            HostName = hostName;
            SessionId = sessionId;
            registry = new PbjPeerRegistry(maxPeers);
            barrier = new TurnBarrier(bridge.CurrentTurn);
            barrier.AddParticipant(PbjPeerRegistry.HostPeerId);
            lobby.AddParticipant(PbjPeerRegistry.HostPeerId);
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

        /// <summary>The save the lobby is gathered around, and how it got there.</summary>
        public LobbySelection Selection => selection;

        public int LobbyReadyCount => lobby.ReadyCount;

        public int LobbyParticipantCount => lobby.ParticipantCount;

        /// <summary>
        /// True once everyone in the lobby has agreed to the selected save.
        /// </summary>
        /// <remarks>
        /// Deliberately inert in M11a: nothing acts on it, and no effect is
        /// emitted when it turns true. Broadcasting "load this save" is M11d's
        /// job, and building the trigger here would mean two mechanisms for one
        /// job. This property and the log line are how the barrier is observed
        /// until then.
        /// </remarks>
        public bool LobbyIsSatisfied => lobby.IsSatisfied;

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
                    // Nothing happens until Hello — an accepted socket is not yet
                    // a peer. It does start a clock, though: see pendingHandshakes.
                    pendingHandshakes[connected.PeerId] = nowSeconds;
                    effects.Add(new LogEffect(NetLog.PeerConnected(connected.PeerId, connected.Remote)));
                    break;

                case PeerBytesEvent:
                    // Decoded by the runtime; never reaches the session raw.
                    break;

                case PeerDisconnectedEvent disconnected:
                    pendingHandshakes.Remove(disconnected.PeerId);
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

                case OrderAppliedEvent applied:
                    HandleOrderApplied(applied);
                    break;

                case SnapshotAppliedEvent:
                    // The host produces snapshots; it never applies one.
                    break;

                case LocalUnreadyEvent:
                    HandleLocalUnready(effects);
                    break;

                case LocalLobbySelectEvent select:
                    HandleLocalLobbySelect(select, effects);
                    break;

                case LocalLobbyReadyEvent:
                    HandleLocalLobbyReady(effects);
                    break;

                case LocalLobbyUnreadyEvent:
                    HandleLocalLobbyUnready(effects);
                    break;

                case CombatEnteredEvent:
                    HandleCombatEntered(effects);
                    break;

                case CombatExitedEvent:
                    HandleCombatExited(effects);
                    break;

                case TickEvent tick:
                    HandleTick(tick, effects);
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

            // Any traffic at all proves the peer is alive, so the stamp goes
            // here rather than in the individual arms.
            if (ticked)
            {
                MarkAlive(peerId);
            }

            switch (message)
            {
                case PongMessage:
                    // Being inbound traffic was its whole job, and that is
                    // already done above.
                    break;

                case HelloMessage hello:
                    HandleHello(peerId, hello, effects);
                    break;

                case RejoinMessage rejoin:
                    HandleRejoin(peerId, rejoin, effects);
                    break;

                case ReadyMessage ready:
                    HandleReady(peerId, ready, effects);
                    break;

                case UnreadyMessage unready:
                    HandleUnready(peerId, unready, effects);
                    break;

                case ScenarioRequestMessage request:
                    HandleScenarioRequest(peerId, request, effects);
                    break;

                case LobbyReadyMessage lobbyReady:
                    HandleLobbyReady(peerId, lobbyReady, effects);
                    break;

                case LobbyUnreadyMessage lobbyUnready:
                    HandleLobbyUnready(peerId, lobbyUnready, effects);
                    break;

                case ByeMessage bye:
                    HandleDisconnect(peerId, Describe(bye.Reason), effects);
                    effects.Add(new DisconnectEffect(peerId, "bye"));
                    break;

                default:
                    // Host-only messages arriving upward, or anything unexpected.
                    effects.Add(new LogEffect(NetLog.PeerLeft(peerId, NameOf(peerId), "protocol violation: " + message.Type)));
                    effects.Add(new DisconnectEffect(peerId, "protocol violation"));
                    KickPeer(peerId, effects);
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
                KickPeer(peerId, effects);
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

            if (RefuseIncompatible(
                    peerId, hello.PlayerName, hello.ModVersion, hello.GameBuild, hello.Passphrase, effects))
            {
                return;
            }

            // A held departure keeps its name. Otherwise a stranger could take a
            // dropped player's name during the grace window, and the real owner's
            // rejoin would be refused as a duplicate through no fault of its own.
            if (IsNameHeldForReconnect(hello.PlayerName))
            {
                Reject(peerId, hello.PlayerName, RejectReason.DuplicateName, "reserved for a reconnect", effects);
                return;
            }

            var refusal = registry.Add(peerId, hello.PlayerName, out var peer);
            if (refusal != null)
            {
                Reject(peerId, hello.PlayerName, refusal.Value, null, effects);
                return;
            }

            // It handshook, so it is a peer now and the registry tracks it.
            pendingHandshakes.Remove(peerId);
            barrier.AddParticipant(peerId);
            lobby.AddParticipant(peerId);

            effects.Add(new SendEffect(peerId, new WelcomeMessage(
                PbjProtocol.Version, SessionId, peerId, HostName, RosterIncludingHost(),
                barrier.Turn, TokenFor(peerId, peer!.Name))));
            effects.Add(new LogEffect(NetLog.HandshakeOk(
                peerId, peer.Name, hello.ProtocolVersion, hello.ModVersion)));
            effects.Add(new BroadcastEffect(new PeerJoinedMessage(peerId, peer.Name), peerId));
            effects.Add(new LogEffect(NetLog.SessionSummary(ParticipantDescriptions())));
            // Broadcast, not sent: one message tells the newcomer the whole
            // lobby and tells everyone else the roster grew. It goes after
            // Welcome so the newcomer knows its own peer id first.
            AnnounceLobby(effects);
            OfferScenario(peerId, effects);

            if (State == HostSessionState.Executing)
            {
                // Welcome carries barrier.Turn, which during execution is a turn
                // already underway. Without this the new peer would sit in
                // Planning and let its player plan a turn that is being run.
                effects.Add(new SendEffect(peerId, new TurnCommitMessage(committedTurn)));
            }

            Reassign(effects);
        }

        /// <summary>
        /// Reclaims a departed peer's units under a new connection.
        /// </summary>
        /// <remarks>
        /// The player is continuous; the peer id is not. Everything downstream —
        /// the barrier, assignments, submissions, keepalive clocks — is keyed on
        /// peer id, so rebinding one entry is far cheaper than making the id space
        /// reusable, and it keeps the invariant that a peer id always addresses
        /// exactly one socket.
        /// </remarks>
        /// <summary>
        /// Refuses a peer this host cannot actually play with. True if it did.
        /// </summary>
        /// <remarks>
        /// Shared by Hello and Rejoin because a returning peer is exactly as
        /// unauthenticated as a new one — the resume token establishes which
        /// departure this is, not that the sender belongs on this listener.
        /// </remarks>
        private bool RefuseIncompatible(
            int peerId,
            string? playerName,
            string? modVersion,
            string? gameBuild,
            string? passphrase,
            List<PbjEffect> effects)
        {
            var fault = PbjProtocol.CheckCompatibility(
                requirements.ModVersion, modVersion,
                requirements.GameBuild, gameBuild,
                requirements.Passphrase, passphrase);
            if (fault == null)
            {
                return false;
            }

            // The detail names both sides for the mismatches, because the whole
            // point is that the operator can see what to change. The passphrase
            // case says nothing extra: a caller that failed it gets no free
            // information about this host.
            string? detail = null;
            if (fault == RejectReason.ModVersionMismatch)
            {
                detail = "peer " + Describe(modVersion) + ", host " + Describe(requirements.ModVersion);
            }
            else if (fault == RejectReason.GameBuildMismatch)
            {
                detail = "peer " + Describe(gameBuild) + ", host " + Describe(requirements.GameBuild);
            }

            Reject(peerId, playerName, fault.Value, detail, effects);
            return true;
        }

        private void HandleRejoin(int peerId, RejoinMessage rejoin, List<PbjEffect> effects)
        {
            if (registry.TryGet(peerId, out _))
            {
                effects.Add(new LogEffect(NetLog.PeerLeft(peerId, NameOf(peerId), "duplicate hello")));
                effects.Add(new DisconnectEffect(peerId, "duplicate hello"));
                KickPeer(peerId, effects);
                return;
            }

            var protocolFault = PbjProtocol.Check(rejoin.Magic, rejoin.ProtocolVersion);
            if (protocolFault != null)
            {
                var detail = protocolFault == RejectReason.VersionMismatch
                    ? "peer v" + rejoin.ProtocolVersion + ", host v" + PbjProtocol.Version
                    : null;
                Reject(peerId, rejoin.PlayerName, protocolFault.Value, detail, effects);
                return;
            }

            if (RefuseIncompatible(
                    peerId, rejoin.PlayerName, rejoin.ModVersion, rejoin.GameBuild, rejoin.Passphrase, effects))
            {
                return;
            }

            if (!string.Equals(rejoin.SessionId, SessionId, StringComparison.Ordinal))
            {
                Reject(peerId, rejoin.PlayerName, RejectReason.UnknownSession, null, effects);
                return;
            }

            // Looked up by the id claimed, then the token checked separately, so
            // possessing a token is not on its own enough to claim a departure.
            if (!departed.TryGetValue(rejoin.ClaimedPeerId, out var previous)
                || !string.Equals(previous.Token, rejoin.ResumeToken, StringComparison.Ordinal))
            {
                Reject(peerId, rejoin.PlayerName, RejectReason.BadResumeToken, null, effects);
                return;
            }

            var refusal = registry.Add(peerId, rejoin.PlayerName, out var peer);
            if (refusal != null)
            {
                // The departed name is reserved while the hold stands, so this is
                // not the "someone took my name" case. Leave the reservation in
                // place so the real owner can still return.
                Reject(peerId, rejoin.PlayerName, refusal.Value, null, effects);
                return;
            }

            departed.Remove(previous.PeerId);
            // It handshook, so it is a peer now and the registry tracks it.
            pendingHandshakes.Remove(peerId);
            barrier.AddParticipant(peerId);
            lobby.AddParticipant(peerId);
            MarkAlive(peerId);

            // Rebind rather than re-plan: re-planning would deal the whole combat
            // again and reshuffle everyone's units mid-fight.
            assignments = assignments.WithPeerRebound(previous.PeerId, peerId);

            effects.Add(new SendEffect(peerId, new WelcomeMessage(
                PbjProtocol.Version, SessionId, peerId, HostName, RosterIncludingHost(),
                barrier.Turn, TokenFor(peerId, peer!.Name))));
            effects.Add(new LogEffect(NetLog.PeerRejoined(previous.PeerId, peerId, peer.Name)));
            effects.Add(new BroadcastEffect(new PeerJoinedMessage(peerId, peer.Name), peerId));
            effects.Add(new LogEffect(NetLog.SessionSummary(ParticipantDescriptions())));
            AnnounceLobby(effects);
            OfferScenario(peerId, effects);

            if (State == HostSessionState.Executing)
            {
                effects.Add(new SendEffect(peerId, new TurnCommitMessage(committedTurn)));
            }

            BroadcastAssignments(effects);
        }

        /// <summary>
        /// Tells a freshly welcomed peer what combat save is available, if any.
        /// </summary>
        /// <remarks>
        /// An offer, not the bytes: at ~124 KB a save is fifty times a snapshot,
        /// and a peer that already holds it — which every rejoining peer does —
        /// should pay nothing. The peer answers with
        /// <see cref="ScenarioRequestMessage"/> only if it wants it.
        /// <para>
        /// Sent unconditionally on handshake rather than only outside combat. The
        /// host does not know why a peer connected, and a peer mid-combat simply
        /// declines; deciding for it here would break the late-join case this is
        /// the groundwork for.
        /// </para>
        /// </remarks>
        private void OfferScenario(int peerId, List<PbjEffect> effects)
        {
            var scenario = bridge.ReadScenario();
            var rejection = scenario.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                // Silence for the ordinary "never saved" case; a warning for a
                // save that exists but is unusable, which is otherwise invisible
                // until someone wonders why the transfer never happened.
                if (rejection != ScenarioRejection.NoFiles)
                {
                    effects.Add(new LogEffect(NetLog.ScenarioNotOffered(scenario.SaveName, rejection)));
                }
                return;
            }

            effects.Add(new SendEffect(peerId, new ScenarioOfferMessage(
                scenario.SaveName, (int)scenario.TotalBytes, scenario.Digest)));
            effects.Add(new LogEffect(NetLog.ScenarioOffered(
                peerId, scenario.SaveName, (int)scenario.TotalBytes, scenario.Digest)));
        }

        /// <summary>
        /// Serves the save on demand.
        /// </summary>
        /// <remarks>
        /// Always sends what the host holds <em>now</em> rather than what the
        /// request names. The digest on the request says which offer is being
        /// answered, but a host that re-saved in between has nothing useful to do
        /// with the old one, and the receiver validates against the digest
        /// carried on the <see cref="ScenarioMessage"/> itself — so answering
        /// with the current save is both simpler and always makes progress.
        /// </remarks>
        private void HandleScenarioRequest(int peerId, ScenarioRequestMessage request, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                return;
            }

            var scenario = bridge.ReadScenario();
            var rejection = scenario.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                effects.Add(new LogEffect(rejection == ScenarioRejection.NoFiles
                    ? NetLog.ScenarioUnavailable()
                    : NetLog.ScenarioNotOffered(scenario.SaveName, rejection)));
                return;
            }

            if (!scenario.Matches(request.Digest) && request.Digest != null)
            {
                effects.Add(new LogEffect(NetLog.ScenarioDigestMismatch(request.Digest, scenario.Digest)));
            }

            effects.Add(new SendEffect(peerId, new ScenarioMessage(
                scenario.SaveName, scenario.Digest, scenario.Files)));
            effects.Add(new LogEffect(NetLog.ScenarioSent(
                peerId, scenario.SaveName, (int)scenario.TotalBytes)));
        }

        /// <summary>
        /// Derives a peer's resume token from the session secret.
        /// </summary>
        /// <remarks>
        /// Deterministic given the secret, so the session stays a pure machine and
        /// tests are repeatable, while remaining uncomputable by anyone who has
        /// only seen the wire. This is a PoC-grade credential, not a cryptographic
        /// one: it binds a returning player to its own units on a listener that
        /// binds 127.0.0.1 by default. See docs/design/networking.md.
        /// </remarks>
        private string TokenFor(int peerId, string name)
        {
            return StateDigest.Mix(sessionSecret + ":" + peerId + ":" + name);
        }

        private void Reject(int peerId, string? name, RejectReason reason, string? detail, List<PbjEffect> effects)
        {
            // Already on its way out; the handshake deadline must not queue a
            // second disconnect for the same socket on the next tick.
            pendingHandshakes.Remove(peerId);
            effects.Add(new SendEffect(peerId, new RejectMessage(reason, detail)));
            effects.Add(new LogEffect(NetLog.HandshakeRejected(name, reason, detail)));
            effects.Add(new DisconnectEffect(peerId, reason.ToString()));
        }

        private void HandleReady(int peerId, ReadyMessage ready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "ready before hello"));
                KickPeer(peerId, effects);
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

        private void HandleUnready(int peerId, UnreadyMessage unready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "unready before hello"));
                KickPeer(peerId, effects);
                return;
            }
            if (State == HostSessionState.Executing)
            {
                effects.Add(new LogEffect(NetLog.UnreadyIgnored(peerId, unready.Turn, "already executing")));
                return;
            }
            if (unready.Turn != barrier.Turn)
            {
                effects.Add(new LogEffect(NetLog.UnreadyIgnored(peerId, unready.Turn, "not the current turn")));
                return;
            }

            // Idempotent by construction: TurnBarrier.Unready tolerates a peer
            // that was never ready, so a duplicate is a no-op rather than a fault.
            barrier.Unready(peerId);
            submitted.Remove(peerId);
            effects.Add(new LogEffect(NetLog.UnreadyReceived(peerId, NameOf(peerId), unready.Turn)));
        }

        private void HandleLocalUnready(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning)
            {
                return;
            }
            if (barrier.Unready(PbjPeerRegistry.HostPeerId))
            {
                effects.Add(new LogEffect(NetLog.UnreadyReceived(
                    PbjPeerRegistry.HostPeerId, HostName, barrier.Turn)));
                effects.Add(new SetExecutionLockEffect(false));
            }
        }

        // --- lobby (M11a) ---

        /// <summary>
        /// The host picked the save the session will play.
        /// </summary>
        /// <remarks>
        /// Always advances the selection version, even when the same save is
        /// re-picked, so every ready clears. One path, no equality branch, and
        /// clearing is the safe direction — the cost is a re-click, the
        /// alternative is loading a save somebody never confirmed.
        /// </remarks>
        private void HandleLocalLobbySelect(LocalLobbySelectEvent select, List<PbjEffect> effects)
        {
            if (State != HostSessionState.Lobby)
            {
                effects.Add(new LogEffect(NetLog.LobbySelectIgnored("not in the lobby")));
                return;
            }

            selection = selection.Next(select.SaveKey, select.SaveDigest);
            lobby.AdvanceTo(selection.Version);

            effects.Add(new LogEffect(selection.HasSave
                ? NetLog.LobbySelected(selection.SaveKey, selection.SaveDigest, selection.Version)
                : NetLog.LobbySelectionCleared(selection.Version)));
            AnnounceLobby(effects);
            ReportLobbyBarrier(effects);
        }

        private void HandleLocalLobbyReady(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Lobby)
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PbjPeerRegistry.HostPeerId, selection.Version, "not in the lobby")));
                return;
            }
            if (!selection.HasSave)
            {
                // Readying for nothing is meaningless, and M11d would be handed a
                // null save key to load.
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PbjPeerRegistry.HostPeerId, selection.Version, "no save selected")));
                return;
            }

            lobby.SetReady(PbjPeerRegistry.HostPeerId, selection.Version);
            effects.Add(new LogEffect(NetLog.LobbyReadyReceived(
                PbjPeerRegistry.HostPeerId, HostName, selection.Version)));
            AnnounceLobby(effects);
            ReportLobbyBarrier(effects);
        }

        private void HandleLocalLobbyUnready(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Lobby || !lobby.Unready(PbjPeerRegistry.HostPeerId))
            {
                return;
            }

            effects.Add(new LogEffect(NetLog.LobbyUnreadyReceived(
                PbjPeerRegistry.HostPeerId, HostName, selection.Version)));
            AnnounceLobby(effects);
        }

        /// <remarks>
        /// Written as if/else rather than a switch over
        /// <see cref="ReadyOutcome"/> for the same reason
        /// <see cref="HandleReady"/> is: a registered peer is always a lobby
        /// participant — membership is added only after <c>registry.Add</c>
        /// succeeds and dropped only in <see cref="RemovePeer"/> — so an arm for
        /// <see cref="ReadyOutcome.UnknownParticipant"/> would be unreachable,
        /// and the coverage gate refuses unreachable code.
        /// </remarks>
        private void HandleLobbyReady(int peerId, LobbyReadyMessage ready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "lobby ready before hello"));
                KickPeer(peerId, effects);
                return;
            }
            if (State != HostSessionState.Lobby)
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    peerId, ready.SelectionVersion, "host is not in the lobby")));
                return;
            }

            var outcome = lobby.SetReady(peerId, ready.SelectionVersion);
            if (outcome == ReadyOutcome.Stale)
            {
                // The host changed the save under them. Their client will clear
                // its own flag when the LobbyState it already sent arrives.
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    peerId, ready.SelectionVersion, "the save has changed since")));
                return;
            }
            if (outcome == ReadyOutcome.NeedsResync)
            {
                // Not the turn barrier's honest "you fell behind": nothing can
                // put a peer ahead of the host's selection. Answer with the truth
                // rather than kicking them, in case the bug is ours.
                effects.Add(new LogEffect(NetLog.LobbyReadyAhead(
                    peerId, ready.SelectionVersion, selection.Version)));
                effects.Add(new SendEffect(peerId, ComposeLobbyState()));
                return;
            }

            effects.Add(new LogEffect(NetLog.LobbyReadyReceived(peerId, NameOf(peerId), ready.SelectionVersion)));
            AnnounceLobby(effects);
            ReportLobbyBarrier(effects);
        }

        private void HandleLobbyUnready(int peerId, LobbyUnreadyMessage unready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "lobby unready before hello"));
                KickPeer(peerId, effects);
                return;
            }
            if (unready.SelectionVersion != selection.Version)
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    peerId, unready.SelectionVersion, "not the current selection")));
                return;
            }

            // Idempotent by construction, like Unready: withdrawing when not
            // ready is a no-op rather than a fault.
            lobby.Unready(peerId);
            effects.Add(new LogEffect(NetLog.LobbyUnreadyReceived(
                peerId, NameOf(peerId), unready.SelectionVersion)));
            AnnounceLobby(effects);
        }

        /// <summary>Publishes the whole lobby to everyone connected.</summary>
        /// <remarks>
        /// Sent on every change including during combat, when the lobby itself
        /// is dormant. Suppressing it there would leave every client's roster
        /// stale at exactly the moment combat ends and the lobby matters again,
        /// and the message is idempotent full state, so a redundant one costs
        /// nothing but bytes.
        /// </remarks>
        private void AnnounceLobby(List<PbjEffect> effects)
        {
            effects.Add(new BroadcastEffect(ComposeLobbyState()));
        }

        private LobbyStateMessage ComposeLobbyState()
        {
            var peers = new LobbyPeerState[registry.Count + 1];
            peers[0] = new LobbyPeerState(
                PbjPeerRegistry.HostPeerId, HostName, lobby.IsReady(PbjPeerRegistry.HostPeerId));
            for (var i = 0; i < registry.Peers.Count; i++)
            {
                var peer = registry.Peers[i];
                peers[i + 1] = new LobbyPeerState(peer.PeerId, peer.Name, lobby.IsReady(peer.PeerId));
            }
            return new LobbyStateMessage(selection.Version, selection.SaveKey, selection.SaveDigest, peers);
        }

        /// <summary>
        /// Says where the lobby barrier stands, but only while it is the thing
        /// in play.
        /// </summary>
        /// <remarks>
        /// Called from every path that can change satisfaction — including
        /// <see cref="HandleDisconnect"/>, because a departing peer can fill the
        /// barrier without anyone readying, exactly as it can for the turn
        /// barrier. Missing that path would leave the M11d trigger unreachable
        /// in the one case where the lobby fills by subtraction.
        /// </remarks>
        private void ReportLobbyBarrier(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Lobby)
            {
                return;
            }
            effects.Add(new LogEffect(lobby.IsSatisfied
                ? NetLog.LobbyBarrierSatisfied(lobby.ParticipantCount, selection.SaveKey)
                : NetLog.LobbyBarrierWaiting(lobby.ReadyCount, lobby.ParticipantCount)));
        }

        private void HandleCombatEntered(List<PbjEffect> effects)
        {
            State = HostSessionState.Planning;
            barrier.AdvanceTo(bridge.CurrentTurn);
            submitted.Clear();
            ClearPendingResults();

            effects.Add(new LogEffect(NetLog.CombatStarted(barrier.Turn, registry.Count)));
            effects.Add(new BroadcastEffect(new CombatStartMessage(barrier.Turn)));

            // Reassign broadcasts Assignments, so "CombatStart followed
            // immediately by Assignments" falls out of the existing path.
            Reassign(effects);
        }

        private void HandleCombatExited(List<PbjEffect> effects)
        {
            State = HostSessionState.Lobby;
            assignments = UnitAssignments.Empty;
            barrier.AdvanceTo(-1);
            submitted.Clear();
            ClearPendingResults();

            // Everyone's lobby readiness predates the fight and means nothing
            // now. Advancing the selection clears it AND makes any LobbyReady
            // still in flight from before the fight arrive as Stale — without
            // this, returning to the lobby finds everybody already "ready".
            selection = selection.Next(selection.SaveKey, selection.SaveDigest);
            lobby.AdvanceTo(selection.Version);

            effects.Add(new LogEffect(NetLog.CombatEnded(registry.Count)));
            effects.Add(new BroadcastEffect(new CombatEndMessage()));
            AnnounceLobby(effects);

            // Unlocks every peer, including any that was sitting Ready when the
            // host's combat resolved.
            effects.Add(new SetExecutionLockEffect(false));
        }

        /// <summary>
        /// Pings quiet peers and drops silent ones.
        /// </summary>
        /// <remarks>
        /// Runs in every state, execution included: <c>Heartbeat.Update</c> keeps
        /// pumping while the simulation runs, and a peer that dies mid-execution
        /// must still be reaped rather than discovered at the next barrier.
        /// </remarks>
        /// <summary>
        /// Drops sockets that connected and then said nothing.
        /// </summary>
        /// <remarks>
        /// Deliberately not folded into the peer-timeout loop below: that walks
        /// the registry, and these are precisely the sockets that never made it
        /// into the registry. Before M7 nothing timed them out at all, which was
        /// a listed limitation and stopped being an acceptable one the moment the
        /// listener could be reached from off the machine.
        /// </remarks>
        private void ExpireSilentHandshakes(List<PbjEffect> effects)
        {
            List<int>? expired = null;
            foreach (var pending in pendingHandshakes)
            {
                if (nowSeconds - pending.Value >= PbjProtocol.HandshakeTimeoutSeconds)
                {
                    expired ??= new List<int>();
                    expired.Add(pending.Key);
                }
            }
            if (expired == null)
            {
                return;
            }

            foreach (var peerId in expired)
            {
                pendingHandshakes.Remove(peerId);
                effects.Add(new LogEffect(NetLog.HandshakeTimedOut(
                    peerId, PbjProtocol.HandshakeTimeoutSeconds)));
                effects.Add(new DisconnectEffect(peerId, "never handshook"));
            }
        }

        private void HandleTick(TickEvent tick, List<PbjEffect> effects)
        {
            nowSeconds = tick.NowSeconds;
            ticked = true;
            ExpireReconnectHolds(effects);
            ExpireSilentHandshakes(effects);

            // Copied, because timing a peer out removes it from the registry.
            var peers = new List<PbjPeer>(registry.Peers);
            foreach (var peer in peers)
            {
                if (!lastInboundSeconds.TryGetValue(peer.PeerId, out var last))
                {
                    // First tick this peer has seen. Seed rather than judge:
                    // in-game the clock starts at whatever the process uptime
                    // happens to be, so treating an unstamped peer as silent
                    // since zero would drop everyone on the very first tick.
                    MarkAlive(peer.PeerId);
                    continue;
                }

                var silent = nowSeconds - last;
                if (silent >= PbjProtocol.PeerTimeoutSeconds)
                {
                    effects.Add(new LogEffect(NetLog.PeerTimedOut(peer.PeerId, peer.Name, silent)));
                    effects.Add(new DisconnectEffect(peer.PeerId, "timeout"));
                    // Through the normal path, so the barrier recomputes and may
                    // commit — a dead peer must never wedge the session.
                    HandleDisconnect(peer.PeerId, "timeout", effects);
                    continue;
                }

                if (nowSeconds - lastPingSeconds[peer.PeerId] >= PbjProtocol.PingIntervalSeconds)
                {
                    lastPingSeconds[peer.PeerId] = nowSeconds;
                    effects.Add(new SendEffect(peer.PeerId, new PingMessage(nextPingNonce++)));
                }
            }
        }

        /// <summary>
        /// Gives up on peers that never came back, and puts their units into play.
        /// </summary>
        /// <remarks>
        /// Not merely a dictionary cleanup: while a hold stands the host does not
        /// re-plan, so this is the moment a permanently-gone player's units are
        /// actually redistributed. Skipping the reassignment here would strand
        /// them for the rest of the combat.
        /// </remarks>
        private void ExpireReconnectHolds(List<PbjEffect> effects)
        {
            if (departed.Count == 0)
            {
                return;
            }

            List<int>? expired = null;
            foreach (var entry in departed)
            {
                if (nowSeconds - entry.Value.DepartedAtSeconds >= PbjProtocol.ReconnectGraceSeconds)
                {
                    (expired ?? (expired = new List<int>())).Add(entry.Key);
                }
            }
            if (expired == null)
            {
                return;
            }

            foreach (var peerId in expired)
            {
                effects.Add(new LogEffect(NetLog.ReconnectExpired(peerId, departed[peerId].Name)));
                departed.Remove(peerId);
            }
            Reassign(effects);
        }

        private bool IsNameHeldForReconnect(string? name)
        {
            if (name == null)
            {
                return false;
            }
            foreach (var entry in departed)
            {
                if (string.Equals(entry.Value.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Records that a peer is alive as of the last tick.
        /// </summary>
        /// <remarks>
        /// Both clocks move together, and always through here — keeping them in
        /// one place is what stops a peer whose first message lands between ticks
        /// from having an inbound stamp but no ping stamp. Resetting the ping
        /// clock on inbound traffic is also the behaviour you want: there is no
        /// reason to probe a peer that just spoke.
        /// </remarks>
        private void MarkAlive(int peerId)
        {
            lastInboundSeconds[peerId] = nowSeconds;
            lastPingSeconds[peerId] = nowSeconds;
        }

        private void HandleOrderApplied(OrderAppliedEvent applied)
        {
            if (applied.Result == OrderApplyResult.Applied)
            {
                if (pendingAccepted.TryGetValue(applied.PeerId, out var count))
                {
                    pendingAccepted[applied.PeerId] = count + 1;
                }
                return;
            }
            Reject(applied.PeerId, applied.BatchIndex, applied.Result);
        }

        private void Reject(int peerId, int batchIndex, OrderApplyResult reason)
        {
            if (pendingRejections.TryGetValue(peerId, out var rejections))
            {
                rejections.Add(new RejectedOrder(batchIndex, reason));
            }
        }

        private void ClearPendingResults()
        {
            pendingAccepted.Clear();
            pendingRejections.Clear();
            pendingResultOrder.Clear();
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
            ClearPendingResults();
            var applied = 0;
            var refused = 0;
            foreach (var peerId in SubmittingPeers())
            {
                // Every submitting peer gets a result, even one whose batch was
                // empty or entirely refused.
                pendingResultOrder.Add(peerId);
                pendingAccepted[peerId] = 0;
                pendingRejections[peerId] = new List<RejectedOrder>();

                var orders = submitted[peerId];
                for (var i = 0; i < orders.Count; i++)
                {
                    var order = orders[i];
                    if (!assignments.IsOwnedBy(peerId, order.OwnerName))
                    {
                        effects.Add(new LogEffect(NetLog.OrderRejectedUnowned(peerId, order.OwnerName)));
                        Reject(peerId, i, OrderApplyResult.NotOwned);
                        refused++;
                        continue;
                    }
                    effects.Add(new ApplyOrderEffect(peerId, i, order));
                    applied++;
                }
            }

            effects.Add(new LogEffect(NetLog.OrdersApplied(applied, refused)));
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
                // No OrderResult: planning re-opens, so these orders have no
                // outcome to report yet. Discarding them also stops a stale
                // rejection attaching itself to the next commit.
                ClearPendingResults();
                effects.Add(new SetExecutionLockEffect(false));
                return;
            }

            State = HostSessionState.Executing;
            submitted.Clear();

            // Results before TurnCommit: a peer should know what became of its
            // orders before it is told execution has begun.
            foreach (var peerId in pendingResultOrder)
            {
                var accepted = pendingAccepted[peerId];
                var rejections = pendingRejections[peerId];
                effects.Add(new SendEffect(peerId, new OrderResultMessage(outcome.Turn, accepted, rejections)));
                effects.Add(new LogEffect(NetLog.OrderResultSent(peerId, accepted, rejections.Count)));
            }
            ClearPendingResults();

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

            // TurnComplete first, then the correction. Snapshot-first would make
            // the client's digest already match by the time it compared, silencing
            // the divergence diagnostic forever — and the client really is
            // diverged at that moment, having not simulated anything.
            effects.Add(new LogEffect(NetLog.SnapshotSent(committedTurn, complete.Units.Count, registry.Count)));
            effects.Add(new BroadcastEffect(
                new SnapshotMessage(committedTurn, complete.Digest, complete.Units)));

            // Keyframes last. They are presentation, and the correction the
            // digest is checked against must never queue behind them. A turn
            // with nothing recorded — prediction disabled, so the game never
            // started its replay recorder — sends nothing at all rather than an
            // empty message a client would have to special-case.
            var keyframes = complete.Keyframes;
            if (keyframes.Tracks.Count > 0)
            {
                var keyCount = 0;
                for (var i = 0; i < keyframes.Tracks.Count; i++)
                {
                    keyCount += keyframes.Tracks[i].Transforms.Count;
                }
                effects.Add(new LogEffect(NetLog.KeyframesSent(
                    committedTurn, keyframes.Tracks.Count, keyCount,
                    keyframes.WindowStart, keyframes.WindowEnd, registry.Count)));
                effects.Add(new BroadcastEffect(new KeyframesMessage(
                    committedTurn, keyframes.WindowStart, keyframes.WindowEnd, keyframes.Tracks)));
            }

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

            // Hold this peer's units instead of re-planning. Reassign would deal
            // the whole combat again over the remaining peers, which destroys the
            // very binding a rejoin needs to reclaim — the units would be gone
            // before the player got back. They stay bound to a peer id no live
            // connection holds, so IsOwnedBy refuses everyone: reserved, visible,
            // uncommandable. Reassignment happens when the grace period expires.
            var holdUnits = ticked && bridge.InCombat && assignments.UnitsFor(peerId).Count > 0;
            if (holdUnits)
            {
                departed[peerId] = new DepartedPeer(peerId, peer.Name, TokenFor(peerId, peer.Name), nowSeconds);
                effects.Add(new LogEffect(NetLog.PeerHeldForReconnect(
                    peerId, peer.Name, PbjProtocol.ReconnectGraceSeconds)));
            }

            // Registry, barrier and submissions still go immediately: a dead peer
            // must never wedge the barrier, whatever happens to its units.
            RemovePeer(peerId);
            effects.Add(new BroadcastEffect(new PeerLeftMessage(peerId, peer.Name)));

            // The roster shrank, so everyone's lobby view is now wrong — and a
            // departing peer can SATISFY the lobby barrier without anybody
            // readying, exactly as it can satisfy the turn barrier below. A
            // caller that only checks after a ready never sees that case.
            AnnounceLobby(effects);
            ReportLobbyBarrier(effects);

            if (!holdUnits)
            {
                Reassign(effects);
            }

            // Removing a peer can satisfy the barrier — a departing peer must
            // never wedge the session.
            if (State == HostSessionState.Planning)
            {
                TryCommit(effects);
            }
        }

        /// <summary>
        /// Forgets a peer everywhere. True if it was actually a member.
        /// </summary>
        /// <remarks>
        /// The return value exists because the roster is now something clients
        /// <em>store and render</em>, not just log: every caller that removes a
        /// real member has to publish the new roster, and the kick paths do not
        /// go through <see cref="HandleDisconnect"/>. Reporting whether anything
        /// was removed keeps that decision at the one place that can know.
        /// </remarks>
        private bool RemovePeer(int peerId)
        {
            var removed = registry.Remove(peerId, out _);
            barrier.RemoveParticipant(peerId);
            lobby.RemoveParticipant(peerId);
            submitted.Remove(peerId);

            // There is nobody left to send a result to, and leaving the entry
            // behind would have the next commit report on a departed peer.
            pendingAccepted.Remove(peerId);
            pendingRejections.Remove(peerId);
            pendingResultOrder.Remove(peerId);

            lastInboundSeconds.Remove(peerId);
            lastPingSeconds.Remove(peerId);
            return removed;
        }

        /// <summary>
        /// Drops a peer we are refusing to keep, and tells everyone left.
        /// </summary>
        /// <remarks>
        /// The kick paths — duplicate hello, protocol violation — never reach
        /// <see cref="HandleDisconnect"/>: by the time the transport reports the
        /// socket closing, the registry entry is already gone and it returns
        /// early. So the roster broadcast has to happen here or a departed peer
        /// haunts every other client's lobby until something unrelated
        /// refreshes it.
        /// </remarks>
        private void KickPeer(int peerId, List<PbjEffect> effects)
        {
            if (RemovePeer(peerId))
            {
                AnnounceLobby(effects);
                ReportLobbyBarrier(effects);
            }
        }

        private void Reassign(List<PbjEffect> effects)
        {
            if (!bridge.InCombat)
            {
                return;
            }
            assignments = UnitAssignmentPlanner.Plan(ParticipantIds(), bridge.AssignableUnitNames);
            effects.Add(new LogEffect(NetLog.Assignment(assignments)));
            BroadcastAssignments(effects);
        }

        /// <summary>
        /// Publishes the current split without re-planning it.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Reassign"/> because a rejoin rebinds one peer
        /// rather than re-dealing the combat, but still has to tell everyone.
        /// Advisory only — every inbound order is re-checked against our own copy.
        /// </remarks>
        private void BroadcastAssignments(List<PbjEffect> effects)
        {
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
