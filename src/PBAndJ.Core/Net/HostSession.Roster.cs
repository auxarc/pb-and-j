using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Membership: who is here, and who has gone.
    //
    // Departures, kicks, reassignment of the units they held, and the views of the
    // roster the rest of the session reads.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
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
            ReviewLobbyAfterDeparture(effects);

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

            // And the same is true of the entry barrier, which is the one the
            // host is standing in a battle waiting on. Last, so that the
            // reassignment StartCombatForEveryone does is the final word rather
            // than something the block above overwrites.
            CompleteCombatEntryIfDone(effects);
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

            // Someone who has gone is not going to report. Without this the load
            // waits out the full timeout on a socket that is already closed —
            // and the entry barrier is worse than the load barrier here, because
            // the host is standing in the battle for every second of it.
            load.Drop(peerId);
            combatEntry.Drop(peerId);
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

            // A kick can be the last thing an entry barrier was waiting on, and
            // the kick paths never reach HandleDisconnect — the registry entry is
            // gone by the time the socket closes.
            CompleteCombatEntryIfDone(effects);
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
