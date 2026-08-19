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
    public sealed partial class HostSession : IPbjSession
    {
        private static readonly UnitPoseTrack[] NoPoses = new UnitPoseTrack[0];

        private readonly IPbjGameBridge bridge;
        private readonly PbjPeerRegistry registry;
        private readonly TurnBarrier barrier;

        /// <summary>
        /// Who has agreed to load the selected save. Separate from
        /// <see cref="barrier"/> on purpose — see <see cref="LobbyBarrier"/>.
        /// </summary>
        private readonly LobbyBarrier lobby = new LobbyBarrier(LobbySelection.None.Version);

        /// <summary>
        /// Whether the campaign has begun and the door has closed. M11e.
        /// </summary>
        /// <remarks>
        /// M11e transfers the save when the lobby selects it, so a peer arriving
        /// after everyone has loaded would never be offered one and could never
        /// ready. Rather than build a second, reactive transfer path for a case
        /// that should not happen, the session stops admitting strangers once it
        /// is under way — and says so, rather than refusing the socket silently.
        /// </remarks>
        private bool lobbySealed;

        /// <summary>
        /// Everyone who has been admitted to this session, by identity.
        /// </summary>
        /// <remarks>
        /// What makes the seal a closed door rather than a wall: a player who
        /// drops out of the overworld campaign — a wifi blip, a crash — must still
        /// be able to come back. The reconnect path cannot carry them, because a
        /// resume token is only minted when the peer held units in combat
        /// (<c>holdUnits</c> below), which is never true in the out-of-campaign
        /// lobby where the seal lives. So the seal admits anyone it has already
        /// seen and refuses only genuine strangers.
        /// <para>
        /// Never pruned. A session's membership is the set of people who have ever
        /// been in it; forgetting someone the moment they disconnect would lock
        /// out exactly the person this exists to let back in.
        /// </para>
        /// </remarks>
        private readonly HashSet<string> admitted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private LobbySelection selection = LobbySelection.None;
        private readonly LoadBarrier load = new LoadBarrier();

        /// <summary>
        /// Who still has to get into the fight the host just entered. M12b.
        /// </summary>
        /// <remarks>
        /// A second <see cref="LoadBarrier"/> rather than a reuse of
        /// <see cref="load"/>: the lobby load and the combat entry can be in
        /// flight for different reasons at different times, and sharing one
        /// would make a stale report for either count toward the other.
        /// </remarks>
        private readonly LoadBarrier combatEntry = new LoadBarrier();
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

        /// <summary>Whether a synchronised load is running.</summary>
        public bool LoadInFlight => load.InFlight;

        /// <summary>
        /// The lobby roster with ready flags — the host at index 0, then peers in
        /// join order.
        /// </summary>
        /// <remarks>
        /// The same list <see cref="ComposeLobbyState"/> puts on the wire, and
        /// deliberately so: a host screen reading a second, separately-built
        /// roster would be a screen that can disagree with what its own clients
        /// were told. <c>ClientSession</c> exposes the received copy under the
        /// same name, which is what lets one <see cref="LobbyView"/> serve both.
        /// </remarks>
        public IReadOnlyList<LobbyPeerState> LobbyRoster
        {
            get
            {
                var peers = new LobbyPeerState[registry.Count + 1];
                peers[0] = new LobbyPeerState(
                    PbjPeerRegistry.HostPeerId, HostName, lobby.IsReady(PbjPeerRegistry.HostPeerId));
                for (var i = 0; i < registry.Peers.Count; i++)
                {
                    var peer = registry.Peers[i];
                    peers[i + 1] = new LobbyPeerState(peer.PeerId, peer.Name, lobby.IsReady(peer.PeerId));
                }
                return peers;
            }
        }

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
    }
}
