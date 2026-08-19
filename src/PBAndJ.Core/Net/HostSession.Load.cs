using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The synchronised load (M11d).
    //
    // Firing it, collecting each peer's report, completing it, and re-reviewing it when
    // somebody leaves while it is in flight.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        /// <summary>
        /// Turns a satisfied lobby into a load, exactly once per agreement.
        /// </summary>
        /// <remarks>
        /// <b>An edge, never a level.</b> <see cref="LobbyBarrier.IsSatisfied"/> is a
        /// predicate over a ready set that nothing consumes, and the host sits in
        /// <see cref="HostSessionState.Lobby"/> for the whole out-of-combat
        /// campaign — so a level-triggered load would re-fire from every later
        /// <see cref="ReportLobbyBarrier"/> call, including the two on the
        /// disconnect and kick paths. One peer dropping mid-campaign would then
        /// reload the original save on every machine and throw away the session's
        /// entire play. Firing therefore <em>consumes</em> the agreement by
        /// advancing the selection, which is what <see cref="HandleCombatExited"/>
        /// already does for the same reason.
        /// <para>
        /// <b>The broadcast order is load-bearing.</b> Advancing puts the host a
        /// version ahead of every client, so the new <c>LobbyState</c> has to go
        /// out first: a client validates <c>LobbyLoad</c> against the version it
        /// last heard, and the host never validates its own. Reverse these two
        /// and every client refuses the load while the host loads alone.
        /// </para>
        /// </remarks>
        private void TryFireLoad(List<PbjEffect> effects)
        {
            if (!lobby.IsSatisfied || load.InFlight || !selection.HasSave)
            {
                return;
            }

            selection = selection.Next(selection.SaveKey, selection.SaveDigest);
            lobby.AdvanceTo(selection.Version);

            var participants = new List<int>(lobby.Participants);
            load.Start(selection.Version, participants);

            effects.Add(new LogEffect(NetLog.LoadStarting(participants.Count, selection.SaveKey)));
            AnnounceLobby(effects);
            effects.Add(new BroadcastEffect(
                new LobbyLoadMessage(selection.Version, selection.SaveKey, selection.SaveDigest)));
            effects.Add(new BeginLoadEffect(selection.SaveKey, selection.Version, selection.SaveDigest));
        }

        private void HandleLobbyLoaded(int peerId, LobbyLoadedMessage loaded, List<PbjEffect> effects)
        {
            if (!load.Report(peerId, loaded.SelectionVersion, loaded.Outcome))
            {
                return;
            }

            effects.Add(new LogEffect(
                NetLog.LoadReported(peerId, NameOf(peerId), loaded.Outcome)));
            CompleteLoadIfDone(effects);
        }

        /// <summary>The host's own load, reported by its own glue.</summary>
        private void HandleLoadFinished(LoadFinishedEvent finished, List<PbjEffect> effects)
        {
            if (!load.Report(PbjPeerRegistry.HostPeerId, finished.SelectionVersion, finished.Outcome))
            {
                return;
            }

            effects.Add(new LogEffect(
                NetLog.LoadReported(PbjPeerRegistry.HostPeerId, HostName, finished.Outcome)));

            // The host is not a peer that can be carried on without — it is the
            // session. If its own load did not happen, nothing did.
            if (finished.Outcome != LoadOutcome.Loaded)
            {
                effects.Add(new LogEffect(NetLog.LoadAbandoned()));
                load.Finish();
                ReportLobbyBarrier(effects);
                return;
            }

            CompleteLoadIfDone(effects);
        }

        /// <summary>
        /// Ends the load once nobody is left to hear from, and hands the lobby
        /// back.
        /// </summary>
        /// <remarks>
        /// Re-running <see cref="ReportLobbyBarrier"/> here is not tidiness. The
        /// lobby keeps accepting readies during a flight, so a lobby that
        /// re-satisfied while the load was running was checked once, refused by
        /// the in-flight guard, and would never be looked at again — fully
        /// agreed, no further messages, wedged.
        /// </remarks>
        private bool CompleteLoadIfDone(List<PbjEffect> effects)
        {
            if (!load.IsComplete)
            {
                return false;
            }

            effects.Add(new LogEffect(
                NetLog.LoadComplete(load.Loaded.Count, lobby.ParticipantCount)));
            load.Finish();

            // M11e: the campaign has begun, so the door closes to newcomers.
            // ⚠️ Sealed HERE and not in TryFireLoad, and the difference matters.
            // A load can be abandoned — the host's own load failing (above) or
            // ExpireLoads timing it out — and both hand the lobby back. Sealing
            // when the load *starts* would leave a session that never entered a
            // campaign refusing joins forever, with nothing to reopen it. Sealing
            // when it completes means only a campaign that actually began closes
            // the door.
            lobbySealed = true;

            ReportLobbyBarrier(effects);
            return true;
        }

        /// <summary>
        /// Re-examines the lobby after somebody has left, whether that ended a
        /// running load or freed one to start.
        /// </summary>
        /// <remarks>
        /// A departure does both jobs and neither implies the other: the last
        /// peer a load was waiting on can leave (which completes it), and the
        /// last peer a lobby was waiting on can leave (which fills it). Reporting
        /// the barrier without first noticing the load had finished left a load
        /// in flight forever with nobody outstanding.
        /// </remarks>
        private void ReviewLobbyAfterDeparture(List<PbjEffect> effects)
        {
            if (!CompleteLoadIfDone(effects))
            {
                ReportLobbyBarrier(effects);
            }
        }
    }
}
