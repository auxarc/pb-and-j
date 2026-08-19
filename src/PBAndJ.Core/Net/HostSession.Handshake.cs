using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Admission: who may join, and as whom.
    //
    // The version check, the identity a name resolves to, the resume token that lets
    // a departed player reclaim their units, and the refusals -- including the one
    // case that is not a refusal at all, a name being held open for a reconnect.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
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

            // M11e's seal. Only on the Hello path: a Rejoin carries a resume token
            // that was issued by this session, so it is self-evidently not a
            // stranger. Refused with a reason rather than a dropped socket —
            // silence here is indistinguishable from the host being down, and this
            // project has paid for that confusion before.
            if (lobbySealed && !admitted.Contains(IdentityOf(hello.PlayerName)))
            {
                Reject(peerId, hello.PlayerName, RejectReason.NotAcceptingPeers,
                    "the campaign is already under way", effects);
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

            // Remembered from here — after every refusal — so only a peer that was
            // genuinely let in can come back through a sealed door.
            admitted.Add(IdentityOf(hello.PlayerName));

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
            TellNewcomerAboutCombat(peerId, effects);

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
            TellNewcomerAboutCombat(peerId, effects);

            if (State == HostSessionState.Executing)
            {
                effects.Add(new SendEffect(peerId, new TurnCommitMessage(committedTurn)));
            }

            BroadcastAssignments(effects);
        }

        /// <summary>
        /// What identifies a peer across disconnects, for M11e's seal.
        /// </summary>
        /// <remarks>
        /// The player name today. The intent is a Steam ID, which is immutable and
        /// cannot collide the way a typed name can — but two things keep it from
        /// being the identity outright, and both are worth stating rather than
        /// discovering later. It would be <b>self-asserted</b>: nothing stops a
        /// peer claiming any ID without Steamworks auth tickets, so it is a
        /// stabler label and not a credential — the passphrase remains the door
        /// lock. And <c>pbj-peer</c> has no Steam at all, yet is the second party
        /// for every selftest that gates <c>make deploy</c>, so a Steam ID can
        /// never be <em>required</em> without taking the harness offline.
        /// <para>
        /// Hence one funnel: when the claim arrives on the wire it is preferred
        /// here and the name stays as the fallback, and nothing else has to change.
        /// </para>
        /// </remarks>
        private static string IdentityOf(string? playerName)
        {
            return playerName ?? string.Empty;
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

        /// <summary>
        /// Tells a peer that joined mid-combat that combat is happening.
        /// </summary>
        /// <remarks>
        /// The 2026-08-03 defect. The accept path sent Welcome, PeerJoined, the
        /// scenario offer and Assignments — and <c>TurnCommit</c> only while
        /// executing — but never <c>CombatStart</c>. So
        /// <c>ClientSession.HandleWelcome</c> fell back to reading the client's
        /// <em>own</em> combat flag, a peer joining from the menu landed in
        /// <c>Lobby</c> with no route out, and its Execute was swallowed forever
        /// by <c>HandleLocalReady</c>'s state guard.
        /// <para>
        /// <b>Its call site is load-bearing in both directions.</b> It must come
        /// <em>after</em> <c>OfferScenario</c>, because <c>CombatStart</c> moves
        /// the client to <c>Planning</c> and <c>HandleScenarioOffer</c> ignores
        /// an offer unless it is in <c>Lobby</c> — send it first and the peer
        /// silently declines the very save it needs. And it must come
        /// <em>before</em> the <c>Executing</c> block, because that sends
        /// <c>TurnCommit</c>: arriving after it, this would unlock a client the
        /// commit had just locked and leave it planning a turn already running.
        /// </para>
        /// </remarks>
        private void TellNewcomerAboutCombat(int peerId, List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning && State != HostSessionState.Executing)
            {
                return;
            }
            effects.Add(new SendEffect(peerId, new CombatStartMessage(barrier.Turn)));
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
    }
}
