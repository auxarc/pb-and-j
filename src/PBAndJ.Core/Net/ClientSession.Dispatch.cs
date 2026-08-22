using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The two entry points. Every case a client acts on arrives at one of them.
    //
    // `Handle` takes a local event, `HandleMessage` a peer message. Some cases they
    // dispatch to a named handler in another part; some they answer inline. Either
    // way, reading the switch is how a case is traced to the code that runs it.
    //
    // `IsFinished` is here too: both of its call sites are the early-out near the top
    // of these two.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
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

                case CombatLoadFinishedEvent combatLoaded:
                    HandleCombatLoadFinished(combatLoaded, effects);
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

                case LocalCombatReadyEvent:
                    // Only a host ships a fight — but the glue that writes one is
                    // armed by an effect and answers frames later, long enough
                    // for the player to have stopped hosting and joined someone
                    // else. Without this arm the default below throws, and
                    // NetGlue.Pump turns a throw into "networking stopped" for
                    // the rest of the process: a stray save would cost the
                    // session.
                    effects.Add(new LogEffect(NetLog.CombatShipNotOurs()));
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

            // Any traffic proves the host is alive. Recorded as a flag as well
            // as stamped, because `nowSeconds` here is the PREVIOUS tick's
            // clock: the runtime drains the mailbox before it ticks, so this
            // stamp predates the drain by however long the last frame took.
            // HandleTick re-stamps from the flag at the clock that judges.
            inboundSinceTick = true;
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
                    HostIsFighting = true;
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
                    HostIsFighting = false;
                    OwnedUnits = NoUnits;
                    submittedThisTurn = false;
                    effects.Add(new LogEffect(NetLog.CombatEndedByHost()));
                    ForgetReplayBuffers();
                    effects.Add(new StopKeyframesEffect());

                    // Held, not released — the combat-retry interregnum. A host
                    // retrying leaves combat first, so this arrives while the
                    // client is still standing in the loaded fight and the host
                    // is seconds from re-entering. Releasing the button here
                    // hands back an Execute that HandleLocalReady drops without
                    // a word, since State is no longer Planning. CombatStart
                    // releases it again on the host's return; Bye and Reject
                    // release it if they never come back.
                    effects.Add(new SetExecutionLockEffect(true));
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

                // M8. Poses precede the keyframes that terminate them, so this
                // only ever accumulates — nothing here decides to play.
                case PosesMessage posesPart:
                    poses.Accept(posesPart);
                    break;

                // M14, and the same shape for the same reason: these precede
                // the keyframes that terminate them, so nothing here decides to
                // play.
                case ReplayAssetsMessage assetsPart:
                    assets.Accept(assetsPart);
                    break;

                case KeyframesMessage keyframes:
                    HandleKeyframes(keyframes, effects);
                    break;

                // M12b. The fight the host just entered. Handled here rather
                // than through M9's ScenarioOffer because that path refuses
                // everything while HostIsFighting -- which is exactly when this
                // arrives, and is why a second message type exists.
                case CombatOfferMessage combatOffer:
                    HandleCombatOffer(combatOffer, effects);
                    break;

                // M12a. No state guard, deliberately: the mirror is presentation,
                // it cannot desynchronise anything by arriving early, and a
                // client whose own ClientSessionState is untrustworthy is a
                // known hazard here — HandleWelcome seeds it from this machine's
                // OWN combat flag, which is how a peer joining mid-fight once
                // locked itself out of the lobby forever.
                case BasePositionMessage basePosition:
                    effects.Add(new MirrorBaseEffect(basePosition.X, basePosition.Z));
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
                    ForgetReplayBuffers();
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

        private bool IsFinished =>
            State == ClientSessionState.Closed || State == ClientSessionState.Faulted;
    }
}
