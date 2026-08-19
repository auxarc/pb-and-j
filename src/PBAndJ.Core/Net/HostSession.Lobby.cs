using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Everything the host does before the fight.
    //
    // Selecting a save, the lobby barrier from both sides of the wire, the lobby state
    // it announces, and the base position the overworld mirrors.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        // --- lobby (M11a) ---

        /// <summary>
        /// Tells everyone where the base is. M12a — the host drives it, so this
        /// is the one direction the position ever travels.
        /// </summary>
        /// <remarks>
        /// Unconditional, and that is deliberate on three counts. It does not
        /// check for peers, because <see cref="BroadcastEffect"/> to an empty
        /// registry is already nothing and a count check here would be a branch
        /// nothing can distinguish. It does not check state, because the base
        /// has a position in the lobby, in the overworld and during a fight, and
        /// a client that stopped hearing about it while the host was busy would
        /// simply be wrong later. And it does not compare against the last value
        /// sent: the glue decides cadence, and a session quietly dropping
        /// updates it judged redundant is how a heartbeat stops being a
        /// heartbeat.
        /// </remarks>
        private void HandleLocalBasePosition(LocalBasePositionEvent basePosition, List<PbjEffect> effects)
        {
            effects.Add(new BroadcastEffect(new BasePositionMessage(basePosition.X, basePosition.Z)));
        }

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

            // Ordering, and it matters for the same reason M11d's does: the offer
            // names a save, and a peer not yet told what the lobby selected has
            // nothing to match it against. AnnounceLobby first, then the bytes —
            // and both before anything that could fire the load.
            AnnounceLobby(effects);
            OfferSelectedSave(effects);
            ReviewLobbyAfterDeparture(effects);
        }

        /// <summary>
        /// Broadcasts the newly selected campaign save, so peers can fetch it
        /// before anyone readies. M11e.
        /// </summary>
        /// <remarks>
        /// The whole reason M11e transfers on selection rather than on a failed
        /// load: a peer only readies once it holds the save, so by the time the
        /// barrier fills every machine can actually load. That is what keeps
        /// <see cref="LoadOutcome.Unavailable"/> off the barrier — which matters
        /// because the barrier completes on failure reports, so the host would
        /// otherwise enter the campaign alone and leave the peer behind.
        /// </remarks>
        private void OfferSelectedSave(List<PbjEffect> effects)
        {
            if (!selection.HasSave
                || string.Equals(selection.SaveKey, LobbySaveNames.ScenarioSlot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var save = bridge.ReadScenario(selection.SaveKey);
            var rejection = save.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                // The host picked a save it cannot send. Worth saying out loud:
                // every peer will sit unready and the lobby will never start.
                effects.Add(new LogEffect(NetLog.ScenarioNotOffered(selection.SaveKey, rejection)));
                return;
            }

            effects.Add(new BroadcastEffect(new ScenarioOfferMessage(
                save.SaveName, (int)save.TotalBytes, save.Digest)));
            effects.Add(new LogEffect(NetLog.ScenarioOffered(
                PbjPeerRegistry.HostPeerId, save.SaveName, (int)save.TotalBytes, save.Digest)));
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
            ReviewLobbyAfterDeparture(effects);
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
            ReviewLobbyAfterDeparture(effects);
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
            return new LobbyStateMessage(
                selection.Version, selection.SaveKey, selection.SaveDigest, LobbyRoster);
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

            TryFireLoad(effects);
        }
    }
}
