using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The two entry points, and only those.
    //
    // `Handle` routes a local event; `HandleMessage` routes a peer message. Reading
    // one of these is how you find which part owns a case.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
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

                case LocalBasePositionEvent basePosition:
                    HandleLocalBasePosition(basePosition, effects);
                    break;

                case LocalCombatReadyEvent combatReady:
                    HandleLocalCombatReady(combatReady, effects);
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

                case LoadFinishedEvent loadFinished:
                    HandleLoadFinished(loadFinished, effects);
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

                case LobbyLoadedMessage lobbyLoaded:
                    HandleLobbyLoaded(peerId, lobbyLoaded, effects);
                    break;

                case CombatEnteredMessage combatEntered:
                    HandleCombatEnteredReport(peerId, combatEntered, effects);
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
    }
}
