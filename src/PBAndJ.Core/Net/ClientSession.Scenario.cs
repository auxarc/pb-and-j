using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Getting the host's save across the wire, and putting it on disk. M9.
    //
    // The offer is declined unless the save is worth fetching; `pbj.scenario-pull`
    // overrides that by hand. HandleScenario is the only place in the mod where bytes
    // off a socket become files, and it checks them before they do.
    //
    // Its last branch is M12b, not M9: if the arriving bytes are the fight
    // ClientSession.CombatEntry.cs was offered, this starts the load.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        /// <summary>
        /// Decides whether the host's save is worth ~124 KB of wire.
        /// </summary>
        /// <remarks>
        /// Two conditions, both deliberately conservative, with
        /// <c>pbj.scenario-pull</c> as the override for everything they exclude:
        /// <list type="bullet">
        /// <item><b>Not while the host is fighting.</b> A client in the host's
        /// combat should not pull a save mid-fight: at best wasted bandwidth, at
        /// worst an invitation to load it and lose the session.</item>
        /// <item><b>Only if we do not already hold it.</b> The client reads its
        /// own save through the same bridge call the host reads its own with, so
        /// this is a local digest comparison and no bytes move. This is what makes
        /// a reconnect free: a rejoining peer holds the save by definition.</item>
        /// </list>
        /// <para>
        /// ⚠️ <b>The first condition reads <see cref="HostIsFighting"/> and not
        /// <see cref="State"/>, and that is the whole point.</b> <see cref="State"/>
        /// is seeded at Welcome from <em>this machine's own</em>
        /// <c>bridge.InCombat</c>, so a player who joins while their own
        /// singleplayer game happens to be mid-combat lands in
        /// <see cref="ClientSessionState.Planning"/> against a host that is merely
        /// in its lobby — and would silently decline every offer for the rest of
        /// the session, never holding the save and so never able to ready. No test
        /// here could catch it: the scripted bridge is never in combat. Gate on
        /// what the host said, never on what we inferred about ourselves — the same
        /// rule M11a set for lobby-ready.
        /// </para>
        /// </remarks>
        private void HandleScenarioOffer(ScenarioOfferMessage offer, List<PbjEffect> effects)
        {
            if (HostIsFighting)
            {
                return;
            }

            var local = bridge.ReadScenario(offer.SaveName);
            if (local.Inspect() == ScenarioRejection.None && local.Matches(offer.Digest))
            {
                effects.Add(new LogEffect(NetLog.ScenarioAlreadyHeld(offer.Digest)));
                return;
            }

            effects.Add(new SendEffect(HostConnectionId, new ScenarioRequestMessage(offer.Digest)));
            effects.Add(new LogEffect(NetLog.ScenarioRequested(offer.Digest)));
        }

        /// <summary>Asks for the host's save regardless of what we hold.</summary>
        private void HandleLocalScenarioPull(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Lobby && State != ClientSessionState.Planning)
            {
                return;
            }

            // No digest: this is "send me whatever you have now", which is the
            // whole point of asking by hand.
            effects.Add(new SendEffect(HostConnectionId, new ScenarioRequestMessage(null)));
            effects.Add(new LogEffect(NetLog.ScenarioRequested(null)));
        }

        /// <summary>
        /// Checks a received save and puts it on disk.
        /// </summary>
        /// <remarks>
        /// The only place in the mod where bytes off a socket become files, so
        /// nothing is taken on trust. The structural checks run first — file
        /// count, total size, allowlisted names — and the digest is recomputed
        /// from the bytes that actually arrived and compared with the one the
        /// sender claimed, so a truncated or substituted transfer is refused
        /// rather than written and then loaded.
        /// <para>
        /// Refusing is not a fault. A peer that sends a bad scenario has not
        /// broken the session, and dropping the connection over it would turn a
        /// recoverable annoyance into a lost game.
        /// </para>
        /// </remarks>
        private void HandleScenario(ScenarioMessage scenario, List<PbjEffect> effects)
        {
            var payload = new ScenarioPayload(scenario.SaveName, scenario.Files);

            var rejection = payload.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                effects.Add(new LogEffect(NetLog.ScenarioRefused(scenario.SaveName, rejection)));
                return;
            }

            if (!payload.Matches(scenario.Digest))
            {
                effects.Add(new LogEffect(NetLog.ScenarioDigestMismatch(scenario.Digest, payload.Digest)));
                return;
            }

            effects.Add(new LogEffect(NetLog.ScenarioReceived(
                scenario.SaveName, payload.Files.Count, payload.TotalBytes)));
            effects.Add(new WriteScenarioEffect(payload));

            // If these are the bytes of a fight we were offered, load them. M9
            // deliberately never auto-loads a scenario -- it tells the player to
            // do it -- but a combat entry is not a suggestion: the host is
            // already in the battle and waiting at a barrier for us.
            if (pendingCombatSave != null
                && string.Equals(pendingCombatSave, scenario.SaveName, StringComparison.Ordinal))
            {
                effects.Add(new BeginCombatLoadEffect(pendingCombatSave, pendingCombatDigest));
            }
        }
    }
}
