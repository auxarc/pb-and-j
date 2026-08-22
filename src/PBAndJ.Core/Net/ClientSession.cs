using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Where to start reading ClientSession, one class split across several files.
    //
    // This part is what a session IS: the fields it is built from, everything it
    // exposes to a screen, and the private helpers that no single part owns. A helper
    // follows a part only when that part holds at least 90% of its call sites; the
    // ones left here top out at 80%.
    //
    // The rest are named for what they handle -- Dispatch, Link, Lobby, Turn,
    // Playback, CombatEntry, Scenario -- and `Handle`/`HandleMessage` in
    // ClientSession.Dispatch.cs are where any case can be traced to its part.

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
    public sealed partial class ClientSession : IPbjSession
    {
        /// <summary>
        /// A client has exactly one connection, so the transport addresses the
        /// host by this fixed id.
        /// </summary>
        public const int HostConnectionId = 0;

        private static readonly int[] HostOnly = { HostConnectionId };
        private static readonly string[] NoUnits = new string[0];
        private static readonly LobbyPeerState[] NoLobbyPeers = new LobbyPeerState[0];

        private readonly PoseBuffer poses = new PoseBuffer();
        private readonly AssetBuffer assets = new AssetBuffer();
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

        // Keepalive. A TickEvent is the only place a clock enters a session, so
        // `nowSeconds` between ticks is the PREVIOUS tick's reading. The runtime
        // drains the mailbox before it ticks, which means HandleMessage stamps
        // `lastInboundSeconds` with a clock that predates the drain — so what
        // arrives is recorded as a flag and the tick that closes the pump does
        // the stamping. See HandleTick in ClientSession.Link.cs.
        private double nowSeconds;
        private double lastInboundSeconds;
        private bool ticked;
        private bool stamped;
        private bool inboundSinceTick;

        /// <summary>The fight we were last offered, so arriving bytes can be matched to it.</summary>
        private string? pendingCombatSave;
        private string? pendingCombatDigest;
        private int pendingCombatTurn = -1;

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

        /// <summary>
        /// Whether a client in this state still owns the outcome of the fight it
        /// is in. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// The predicate the mod's <c>ScenarioUtility.EndCombatWithOutcome</c>
        /// prefix is armed by. It lives here, as a pure function of the state
        /// enum declared above it, because it is a decision and the mod glue that
        /// asks it is outside the coverage gate — a wrong <c>if</c> there costs
        /// the whole feature and nothing would fail the build.
        /// <para>
        /// 🔴 <b><see cref="ClientSessionState.Closed"/> and
        /// <see cref="ClientSessionState.Faulted"/> are excluded as a correctness
        /// requirement, not as tidiness.</b> <see cref="Fault"/>'s own comment
        /// states the intent: a lost host must never leave the local execute
        /// button disabled, because the player continues single-player from
        /// there. That human then simulates locally, <c>CombatExecutionEndSystem</c>
        /// fires, the victory count runs — and a prefix still armed would eat the
        /// outcome and make the fight <b>unwinnable and unlosable for ever</b>.
        /// A <c>Bye</c> reaches the same place.
        /// </para>
        /// </remarks>
        public static bool ClientOwnsCombatOutcome(ClientSessionState state)
        {
            return state != ClientSessionState.Closed
                && state != ClientSessionState.Faulted;
        }

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

        /// <summary>
        /// Whether the <em>host</em> has said it is fighting.
        /// </summary>
        /// <remarks>
        /// Set only by <c>CombatStart</c> and <c>CombatEnd</c>, so it says what the
        /// host said and nothing about this machine. <see cref="State"/> cannot be
        /// used for this: it is seeded at Welcome from our own
        /// <c>bridge.InCombat</c>, which is a fact about the player's local game and
        /// not about the session. See <see cref="HandleScenarioOffer"/>.
        /// </remarks>
        public bool HostIsFighting { get; private set; }

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

        /// <summary>
        /// Drops everything held for a turn that will never be terminated.
        /// </summary>
        /// <remarks>
        /// One call rather than two, because the two buffers are terminated by
        /// the same message and must therefore be abandoned by the same events.
        /// A site that remembered one and forgot the other would leave a turn's
        /// effects waiting on a <c>Keyframes</c> that a rejoin's first turn
        /// would then consume — replaying the previous session's explosions
        /// over the new one's opening move.
        /// </remarks>
        private void ForgetReplayBuffers()
        {
            poses.Clear();
            assets.Clear();
        }

        private void Fault(string line, List<PbjEffect> effects)
        {
            effects.Add(new LogEffect(line));
            State = ClientSessionState.Faulted;
            // A faulted session handles nothing further, so a playback left
            // running here would never be stopped by anything else — units would
            // slide along last turn's path into single-player. The held poses go
            // with it: nothing will ever send the terminator that would consume
            // them, and a fault is followed by a rejoin often enough that
            // leaving them is a real hazard rather than a tidy-up.
            ForgetReplayBuffers();
            effects.Add(new StopKeyframesEffect());
            // A lost host must never leave the local execute button disabled —
            // the player continues single-player from here.
            effects.Add(new SetExecutionLockEffect(false));
        }

        private static string Describe(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value!;
    }
}
