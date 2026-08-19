using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Getting everyone into the fight, and out again.
    //
    // Includes the newcomer who arrives after it has started, and every way an entry is
    // abandoned rather than completed.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        /// <summary>
        /// The host is in a fight. Nobody else is yet. M12b.
        /// </summary>
        /// <remarks>
        /// <b>This deliberately no longer broadcasts CombatStart</b>, and the
        /// reason is a deadlock rather than a preference. CombatStart sets
        /// <c>HostIsFighting</c> on every client, and
        /// <c>ClientSession.HandleScenarioOffer</c> refuses every offer while
        /// that is true — a guard built on purpose, since pulling a save
        /// mid-fight is normally nonsense. Announcing the fight before shipping
        /// it therefore guarantees nobody can fetch it: the host offers, every
        /// client silently declines, and two people watch a spinner.
        /// <para>
        /// So the announcement waits. The glue writes the fight to the scenario
        /// slot and says so (<see cref="LocalCombatReadyEvent"/>), the fight is
        /// offered, everyone loads it and reports in, and only then does
        /// CombatStart go out — where it keeps its existing meaning of "the
        /// fight has begun, plan your turn", which is what M11's turn barrier
        /// already assumes.
        /// </para>
        /// </remarks>
        private void HandleCombatEntered(List<PbjEffect> effects)
        {
            State = HostSessionState.Planning;
            barrier.AdvanceTo(bridge.CurrentTurn);
            submitted.Clear();
            ClearPendingResults();

            effects.Add(new LogEffect(NetLog.CombatShipping(barrier.Turn, registry.Count)));

            // Asked for whatever the peer count is, and the reason is the
            // scenario slot rather than this fight: OfferScenario hands that slot
            // to every newcomer, so a write skipped because nobody was listening
            // would be offered to the next peer to arrive — the previous
            // mission's battle, under this mission's name. One synchronous save
            // is the cheaper of those two.
            effects.Add(new ShipCombatEffect());
        }

        /// <summary>
        /// The fight is on disk. Offer it, and wait for everyone to arrive.
        /// </summary>
        private void HandleLocalCombatReady(LocalCombatReadyEvent ready, List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning)
            {
                // A fight written after we left it. The glue disarms itself on the
                // combat edge, so this should not arrive — but "should not" is
                // what the entry barrier believed about late reports, and acting
                // on one here would announce a fight at turn -1 from a host
                // sitting in its lobby. Say so and drop it.
                effects.Add(new LogEffect(NetLog.CombatShipTooLate()));
                return;
            }

            if (string.IsNullOrEmpty(ready.SaveName) || string.IsNullOrEmpty(ready.Digest))
            {
                // The glue could not write the fight. Reachable: CanSave has
                // several refusals the poll cannot outwait. Starting alone is
                // wrong for the session but right for the human at this machine,
                // who is already in a battle and cannot be left staring at it.
                effects.Add(new LogEffect(NetLog.CombatShipFailed()));

                // "Alone" has to mean alone. Keeping the peers connected while
                // starting without them is the worse of the two failures: they
                // were never offered a fight they could join, and every one of
                // them would sit in the turn barrier holding it shut for the rest
                // of the battle.
                DropEveryoneFromTheFight("the fight could not be shared", effects);
                StartCombatForEveryone(effects);
                return;
            }

            var peers = new List<int>();
            foreach (var peer in registry.Peers)
            {
                peers.Add(peer.PeerId);
            }

            if (peers.Count == 0)
            {
                // Nobody to wait for. Said out loud rather than silently skipped,
                // because "the fight was never offered" and "everyone arrived
                // instantly" look identical in a log otherwise.
                effects.Add(new LogEffect(NetLog.CombatNobodyToWaitFor()));
                StartCombatForEveryone(effects);
                return;
            }

            combatEntry.Start(barrier.Turn, peers);
            combatEntry.Seed(nowSeconds);

            effects.Add(new LogEffect(NetLog.CombatOffered(ready.SaveName, ready.Digest, peers.Count)));
            effects.Add(new BroadcastEffect(
                new CombatOfferMessage(ready.SaveName, ready.Digest, barrier.Turn)));
        }

        /// <summary>A client reporting whether it got into the fight.</summary>
        private void HandleCombatEnteredReport(int peerId, CombatEnteredMessage report, List<PbjEffect> effects)
        {
            if (!combatEntry.Report(peerId, report.Turn, report.Outcome))
            {
                return;
            }

            effects.Add(new LogEffect(NetLog.CombatEntryReported(peerId, NameOf(peerId), report.Outcome)));

            if (report.Outcome != LoadOutcome.Loaded)
            {
                // It said so itself: it is not in the fight. Leaving it connected
                // would leave it holding the turn barrier shut and holding units
                // nobody can command — a wedge that outlasts the battle. The
                // session is what it loses, and a session is rejoinable.
                DropFromTheFight(peerId, "could not get into the fight", effects);
            }

            CompleteCombatEntryIfDone(effects);
        }

        /// <summary>
        /// Drops a peer that is not in the fight everyone else is entering.
        /// </summary>
        /// <remarks>
        /// <b>Not merely un-awaited.</b> The entry barrier and the registry are
        /// different sets: dropping someone from the first stops the fight
        /// waiting on it, and leaves it in the second — where
        /// <c>ParticipantIds</c> still deals it units and the <em>turn</em>
        /// barrier still waits for a ready it can never send, for every turn of
        /// the battle. <see cref="StartCombatForEveryone"/> said this was already
        /// handled; it was not, until here.
        /// <para>
        /// The same composition every other kick uses — say why, close the
        /// socket, forget them — so a dropped peer learns of it the same way and
        /// can rejoin by the same door.
        /// </para>
        /// </remarks>
        private void DropFromTheFight(int peerId, string reason, List<PbjEffect> effects)
        {
            effects.Add(new LogEffect(NetLog.PeerLeft(peerId, NameOf(peerId), reason)));
            effects.Add(new DisconnectEffect(peerId, reason));
            KickPeer(peerId, effects);
        }

        private void DropEveryoneFromTheFight(string reason, List<PbjEffect> effects)
        {
            // Over a copy: KickPeer mutates the registry underneath.
            var leaving = new List<int>();
            foreach (var peer in registry.Peers)
            {
                leaving.Add(peer.PeerId);
            }

            for (var i = 0; i < leaving.Count; i++)
            {
                DropFromTheFight(leaving[i], reason, effects);
            }
        }

        private void CompleteCombatEntryIfDone(List<PbjEffect> effects)
        {
            if (!combatEntry.IsComplete)
            {
                return;
            }

            combatEntry.Finish();
            StartCombatForEveryone(effects);
        }

        /// <summary>
        /// Announces the fight to everyone still here and hands out units.
        /// </summary>
        /// <remarks>
        /// What <see cref="HandleCombatEntered"/> used to do inline. A peer that
        /// never made it is not in the registry's answer by now — it reported a
        /// failure, or timed out, or dropped, and every one of those roads goes
        /// through <see cref="DropFromTheFight"/> — so the assignments cover
        /// exactly the machines that are actually in the fight.
        /// <para>
        /// ⚠️ <b>This comment described an invariant the code did not have.</b>
        /// Until M12b·2 the failure and timeout paths dropped a peer from the
        /// entry barrier only, leaving it in the registry: dealt units, and
        /// awaited by the <em>turn</em> barrier for every turn of a battle it was
        /// never in. Nobody could execute again. The invariant is real now, and
        /// anything added here has to keep it.
        /// </para>
        /// </remarks>
        private void StartCombatForEveryone(List<PbjEffect> effects)
        {
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

            // A fight abandoned while people were still loading into it must not
            // be able to start later. Nothing else guards this: a report arriving
            // after the exit would satisfy the entry barrier and broadcast
            // CombatStart at turn -1, from a host sitting in its lobby.
            if (combatEntry.InFlight)
            {
                effects.Add(new LogEffect(NetLog.CombatEntryAbandoned(combatEntry.Waiting.Count)));
                combatEntry.Finish();
            }

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
    }
}
