using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The lobby: agreeing on a save, and loading it together.
    //
    // The host owns the selection. This side displays it, decides whether it can
    // honour it, agrees or withdraws, and reports how the load went.
    // `HoldsSelectedSave` is that decision, and it compares digests rather than names.
    //
    // This is not everything that happens before the fight -- the handshake is in
    // ClientSession.Link.cs and the save transfer in ClientSession.Scenario.cs.
    //
    // Provenance, dated with `git log -S`: 84487d0 (M11a) opened this section,
    // 7d5d5ce (M11d) added the synchronised load -- HandleLobbyLoad and
    // HandleLoadFinished -- and 9a04be9 (M11e) added HoldsSelectedSave. It replaces a
    // `// --- lobby (M11a) ---` banner, whose tag had stopped being true of the
    // members added under it after M11a.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        /// <summary>Takes the host's view of the lobby wholesale.</summary>
        /// <remarks>
        /// Full state, so there is nothing to merge and no ordering hazard
        /// between "who joined" and "who is ready" — the same reason
        /// <c>Assignments</c> is a full replacement.
        /// </remarks>
        private void HandleLobbyState(LobbyStateMessage state, List<PbjEffect> effects)
        {
            if (state.SelectionVersion != LobbySelectionVersion)
            {
                // A new selection clears everyone's agreement on the host, so
                // ours is gone too whether we asked or not.
                LobbyReadySent = false;
            }

            LobbySelectionVersion = state.SelectionVersion;
            LobbySaveKey = state.SaveKey;
            LobbySaveDigest = state.SaveDigest;
            LobbyRoster = state.Peers;

            var readyCount = 0;
            for (var i = 0; i < state.Peers.Count; i++)
            {
                if (state.Peers[i].Ready)
                {
                    readyCount++;
                }
            }

            effects.Add(new LogEffect(NetLog.LobbyStateReceived(
                state.SelectionVersion, state.SaveKey, readyCount, state.Peers.Count)));
        }

        /// <summary>
        /// The host says everyone agreed. Start loading.
        /// </summary>
        /// <remarks>
        /// Gated on data rather than on <see cref="State"/>, the M11a lesson:
        /// <c>HandleWelcome</c> derives that state from this client's <em>own</em>
        /// combat flag, so it is not something to make a destructive decision on.
        /// </remarks>
        private void HandleLobbyLoad(LobbyLoadMessage load, List<PbjEffect> effects)
        {
            if (load.SelectionVersion != LobbySelectionVersion)
            {
                effects.Add(new LogEffect(NetLog.LoadIgnoredStale(
                    load.SelectionVersion, LobbySelectionVersion)));
                return;
            }
            if (load.SelectionVersion == LoadBegunVersion)
            {
                effects.Add(new LogEffect(NetLog.LoadAlreadyBegun(load.SelectionVersion)));
                return;
            }

            LoadBegunVersion = load.SelectionVersion;
            effects.Add(new BeginLoadEffect(load.SaveKey, load.SelectionVersion, LobbySaveDigest));
        }

        private void HandleLoadFinished(LoadFinishedEvent finished, List<PbjEffect> effects)
        {
            effects.Add(new SendEffect(
                HostConnectionId,
                new LobbyLoadedMessage(finished.SelectionVersion, finished.Outcome)));
        }

        /// <summary>
        /// Agrees to load the selected save.
        /// </summary>
        /// <remarks>
        /// Gated on <em>data</em> — a selection we have actually been told about
        /// — and deliberately NOT on <see cref="ClientSessionState.Lobby"/>.
        /// <see cref="HandleWelcome"/> sets the state from this client's OWN
        /// <c>bridge.InCombat</c>, so a player who joins while their local game
        /// happens to be mid-combat is welcomed straight into
        /// <see cref="ClientSessionState.Planning"/>. A state guard would then
        /// refuse them the lobby forever, holding a perfectly good
        /// <c>LobbyState</c>, and no harness test would ever catch it because
        /// the scripted bridge is never in combat.
        /// <para>
        /// The host re-checks its own state regardless, so this being permissive
        /// costs nothing: a ready sent at the wrong moment is logged and dropped
        /// there rather than counted.
        /// </para>
        /// </remarks>
        private void HandleLocalLobbyReady(List<PbjEffect> effects)
        {
            if (LobbySelectionVersion < 0)
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PeerId, LobbySelectionVersion, "no lobby state received yet")));
                return;
            }
            if (string.IsNullOrEmpty(LobbySaveKey))
            {
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PeerId, LobbySelectionVersion, "the host has not picked a save")));
                return;
            }
            if (!HoldsSelectedSave())
            {
                // M11e. Readying without the save is what M11d's barrier cannot
                // survive: the host would fire the load, this peer would report
                // Unavailable, and the barrier completes on failure reports — so
                // everyone else enters the campaign and this one is left behind
                // with no way back in once the lobby seals. Readying is the promise
                // that we can actually load, so it waits for the bytes.
                effects.Add(new LogEffect(NetLog.LobbyReadyIgnored(
                    PeerId, LobbySelectionVersion, "still waiting for the save to arrive")));
                return;
            }

            LobbyReadySent = true;
            effects.Add(new SendEffect(HostConnectionId, new LobbyReadyMessage(LobbySelectionVersion)));
            effects.Add(new LogEffect(NetLog.LobbyReadyReceived(PeerId, playerName, LobbySelectionVersion)));
        }

        /// <summary>
        /// Whether this machine holds the save the lobby selected.
        /// </summary>
        /// <remarks>
        /// By digest and not by name, because same-name-different-contents is
        /// exactly the case that would diverge silently: everyone loads "their"
        /// copy and the campaigns drift apart with nothing to notice it. A lobby
        /// that has published no digest yet cannot be checked against, so the name
        /// alone has to do — the host re-offers on every selection, so this
        /// resolves as soon as the digest arrives.
        /// </remarks>
        private bool HoldsSelectedSave()
        {
            var local = bridge.ReadScenario(LobbySaveKey);
            if (local.Inspect() != ScenarioRejection.None)
            {
                return false;
            }
            return string.IsNullOrEmpty(LobbySaveDigest) || local.Matches(LobbySaveDigest);
        }

        private void HandleLocalLobbyUnready(List<PbjEffect> effects)
        {
            if (!LobbyReadySent)
            {
                return;
            }

            LobbyReadySent = false;
            effects.Add(new SendEffect(HostConnectionId, new LobbyUnreadyMessage(LobbySelectionVersion)));
            effects.Add(new LogEffect(NetLog.LobbyUnreadyReceived(PeerId, playerName, LobbySelectionVersion)));
        }
    }
}
