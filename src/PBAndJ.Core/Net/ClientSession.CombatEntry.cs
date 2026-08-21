using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Joining the fight the host is already in. M12b.
    //
    // A separate path from the M9 scenario offer in ClientSession.Scenario.cs, and
    // the source says why: that one refuses everything while HostIsFighting, which is
    // exactly when this arrives.
    //
    // When the bytes are not already held this asks for them and stops. The load is
    // then started from ClientSession.Scenario.cs, when they land -- which is why the
    // pendingCombat fields sit in ClientSession.cs and not here.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        /// <summary>
        /// The fight the host is in, offered so we can join it. M12b.
        /// </summary>
        /// <remarks>
        /// Same shape as M9's scenario offer — hold it already, or ask for the
        /// bytes — with one difference that matters: the name alone is not
        /// enough. The scenario slot is rewritten at the start of every mission,
        /// so a client can be holding the <em>previous</em> fight under exactly
        /// this name, and only the digest tells them apart.
        /// </remarks>
        private void HandleCombatOffer(CombatOfferMessage offer, List<PbjEffect> effects)
        {
            pendingCombatSave = offer.SaveName;
            pendingCombatDigest = offer.Digest;
            pendingCombatTurn = offer.Turn;

            var local = bridge.ReadScenario(offer.SaveName);
            if (local.Inspect() == ScenarioRejection.None && local.Matches(offer.Digest))
            {
                effects.Add(new LogEffect(NetLog.CombatAlreadyHeld(offer.SaveName)));
                effects.Add(new BeginCombatLoadEffect(offer.SaveName, offer.Digest));
                return;
            }

            effects.Add(new LogEffect(NetLog.CombatFetching(offer.SaveName)));

            // ⚠️ The DIGEST, never the name. A request identifies a save by its
            // bytes precisely so a peer cannot name a file on the host's disk --
            // HostSession.ResolveRequested matches against the digests it has
            // already decided to offer and grants the peer no say. Passing the
            // name here type-checks (both are string?) and then fails entirely on
            // the far side: the match misses, the host falls through to "the
            // lobby's campaign wins", and the client is sent the campaign save
            // when it asked for the fight. Found by the first two-instance
            // playtest, 2026-08-14.
            effects.Add(new SendEffect(HostConnectionId, new ScenarioRequestMessage(offer.Digest)));
        }

        /// <summary>
        /// Reports how the attempt to join the fight went. M12b.
        /// </summary>
        /// <remarks>
        /// Sent even when it failed, and that is the point: the load callback is
        /// success-only, so a client that says nothing costs the host its whole
        /// timeout. A client that knows it could not join says so at once and the
        /// fight starts without it.
        /// </remarks>
        private void HandleCombatLoadFinished(CombatLoadFinishedEvent finished, List<PbjEffect> effects)
        {
            effects.Add(new SendEffect(
                HostConnectionId, new CombatEnteredMessage(pendingCombatTurn, finished.Outcome)));
        }
    }
}
