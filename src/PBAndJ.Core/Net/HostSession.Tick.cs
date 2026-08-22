using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The clock, and everything it expires.
    //
    // Each expiry has the same shape -- a deadline compared against `nowSeconds` -- and
    // `HandleTick` is the only caller of all of them.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
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

        /// <summary>
        /// Pings quiet peers and drops silent ones.
        /// </summary>
        /// <remarks>
        /// Runs in every state, execution included: <c>Heartbeat.Update</c> keeps
        /// pumping while the simulation runs, and a peer that dies mid-execution
        /// must still be reaped rather than discovered at the next barrier.
        /// </remarks>
        private void HandleTick(TickEvent tick, List<PbjEffect> effects)
        {
            nowSeconds = tick.NowSeconds;
            ticked = true;
            ExpireLoads(effects);
            ExpireCombatEntry(effects);
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

                if (inboundSinceTick.Contains(peer.PeerId))
                {
                    // Spoke during the pump this tick closes. HandleMessage
                    // stamped it with the PREVIOUS tick's clock — the only one
                    // a session has between ticks — so re-stamp at the clock
                    // that judges. Without this the silence below is measured
                    // from before a stall that the proof of life arrived
                    // during: one 20s frame gap on THIS machine dropped a peer
                    // that had just answered a ping, and the host went on to
                    // report that the fight could not be shared.
                    //
                    // A restart, not a reprieve: a peer that speaks once and
                    // then dies is still reaped a full PeerTimeoutSeconds after
                    // this.
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

            // Emptied rather than pruned per peer: ids in here that the loop
            // above did not visit belong to sockets that are not peers — a
            // handshake in flight, or one already dropped — and leaving them
            // would credit a stranger's silence to whoever inherits the id.
            inboundSinceTick.Clear();
        }

        /// <summary>
        /// Starts the fight without anyone who never said they got in. M12b.
        /// </summary>
        /// <remarks>
        /// The same seed-don't-judge shape as <see cref="ExpireLoads"/>, and for
        /// the same reason: the combat offer goes out from a message handler, and
        /// a deadline judged against process uptime would expire on the first
        /// tick.
        /// <para>
        /// Unlike the lobby load, the host is never a participant here — it is
        /// already in the fight, which is what started all this — so every
        /// expiry is simply a peer to carry on without.
        /// </para>
        /// </remarks>
        private void ExpireCombatEntry(List<PbjEffect> effects)
        {
            if (!combatEntry.InFlight)
            {
                return;
            }

            combatEntry.Seed(nowSeconds);
            var expired = combatEntry.Expired(nowSeconds, PbjProtocol.LoadTimeoutSeconds);
            for (var i = 0; i < expired.Count; i++)
            {
                effects.Add(new LogEffect(NetLog.CombatEntryTimedOut(expired[i])));

                // Dropped from the session, not merely from this barrier — see
                // DropFromTheFight. The last one dropped completes the entry from
                // inside KickPeer, by which point every other expired peer has
                // already gone, so the fight starts over exactly the machines
                // that are in it.
                DropFromTheFight(expired[i], "never got into the fight", effects);
            }

            CompleteCombatEntryIfDone(effects);
        }

        /// <summary>
        /// Gives everyone in a running load a deadline, and gives up on whoever
        /// blows it.
        /// </summary>
        /// <remarks>
        /// Deadlines are minted here rather than when the load fires, because a
        /// load fires from a message handler where the clock may not have been
        /// stamped yet — and a deadline of zero plus the timeout, judged against
        /// process uptime, expires the whole session on the first tick. Same
        /// seed-don't-judge shape the keepalive uses two methods down.
        /// <para>
        /// The host is included, and it is the one participant a timeout cannot
        /// simply drop: the others are already in a campaign it is not in.
        /// </para>
        /// </remarks>
        private void ExpireLoads(List<PbjEffect> effects)
        {
            if (!load.InFlight)
            {
                return;
            }

            load.Seed(nowSeconds);
            var expired = load.Expired(nowSeconds, PbjProtocol.LoadTimeoutSeconds);
            if (expired.Count == 0)
            {
                return;
            }

            var hostExpired = false;
            for (var i = 0; i < expired.Count; i++)
            {
                effects.Add(new LogEffect(NetLog.LoadTimedOut(expired[i])));
                load.Drop(expired[i]);
                hostExpired |= expired[i] == PbjPeerRegistry.HostPeerId;
            }

            if (hostExpired)
            {
                effects.Add(new LogEffect(NetLog.LoadAbandoned()));
                load.Finish();
                ReportLobbyBarrier(effects);
                return;
            }

            CompleteLoadIfDone(effects);
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
    }
}
