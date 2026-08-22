using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Moving the scenario or the save itself.
    //
    // Offers, requests, and resolving a requested digest to the bytes that answer it.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        /// <summary>
        /// Tells a freshly welcomed peer what combat save is available, if any.
        /// </summary>
        /// <remarks>
        /// An offer, not the bytes: at ~124 KB a save is fifty times a snapshot,
        /// and a peer that already holds it — which every rejoining peer does —
        /// should pay nothing. The peer answers with
        /// <see cref="ScenarioRequestMessage"/> only if it wants it.
        /// <para>
        /// Sent unconditionally on handshake rather than only outside combat. The
        /// host does not know why a peer connected, and a peer mid-combat simply
        /// declines; deciding for it here would break the late-join case this is
        /// the groundwork for.
        /// </para>
        /// </remarks>
        private void OfferScenario(int peerId, List<PbjEffect> effects)
        {
            OfferSave(peerId, LobbySaveNames.ScenarioSlot, effects);

            // M11e: the lobby's campaign save, when one has been chosen. A peer
            // joining after the selection is otherwise never offered it and can
            // never ready — and nothing times a lobby out, so the whole thing would
            // sit there fully connected and never start.
            var selected = selection.SaveKey;
            if (!string.IsNullOrEmpty(selected)
                && !LobbySaveNames.IsNonCampaignSlot(selected))
            {
                OfferSave(peerId, selected, effects);
            }
        }

        /// <summary>
        /// Offers one named save to one peer. M11e.
        /// </summary>
        private void OfferSave(int peerId, string? saveKey, List<PbjEffect> effects)
        {
            var scenario = bridge.ReadScenario(saveKey);
            var rejection = scenario.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                // Silence for the ordinary "never saved" case; a warning for a
                // save that exists but is unusable, which is otherwise invisible
                // until someone wonders why the transfer never happened.
                if (rejection != ScenarioRejection.NoFiles)
                {
                    effects.Add(new LogEffect(NetLog.ScenarioNotOffered(scenario.SaveName, rejection)));
                }
                return;
            }

            effects.Add(new SendEffect(peerId, new ScenarioOfferMessage(
                scenario.SaveName, (int)scenario.TotalBytes, scenario.Digest)));
            effects.Add(new LogEffect(NetLog.ScenarioOffered(
                peerId, scenario.SaveName, (int)scenario.TotalBytes, scenario.Digest)));
        }

        /// <summary>
        /// Serves the save on demand.
        /// </summary>
        /// <remarks>
        /// Always sends what the host holds <em>now</em> rather than what the
        /// request names. The digest on the request says which offer is being
        /// answered, but a host that re-saved in between has nothing useful to do
        /// with the old one, and the receiver validates against the digest
        /// carried on the <see cref="ScenarioMessage"/> itself — so answering
        /// with the current save is both simpler and always makes progress.
        /// </remarks>
        private void HandleScenarioRequest(int peerId, ScenarioRequestMessage request, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                return;
            }

            var scenario = ResolveRequested(request.Digest);
            var rejection = scenario.Inspect();
            if (rejection != ScenarioRejection.None)
            {
                effects.Add(new LogEffect(rejection == ScenarioRejection.NoFiles
                    ? NetLog.ScenarioUnavailable()
                    : NetLog.ScenarioNotOffered(scenario.SaveName, rejection)));
                return;
            }

            if (!scenario.Matches(request.Digest) && request.Digest != null)
            {
                effects.Add(new LogEffect(NetLog.ScenarioDigestMismatch(request.Digest, scenario.Digest)));
            }

            effects.Add(new SendEffect(peerId, new ScenarioMessage(
                scenario.SaveName, scenario.Digest, scenario.Files)));
            effects.Add(new LogEffect(NetLog.ScenarioSent(
                peerId, scenario.SaveName, (int)scenario.TotalBytes)));
        }

        /// <summary>
        /// Which of the host's saves a request is asking for. M11e.
        /// </summary>
        /// <remarks>
        /// <b>Resolved by digest against the saves this host would offer, never by
        /// a name off the wire.</b> A request could have carried the key, and that
        /// would have put the <em>host</em> in the position M9 spent three guards
        /// avoiding on the receiving side — reading its own disk under a name a peer
        /// chose. Matching a digest against the short list we already decided to
        /// offer gives the same answer and grants a peer no say in it.
        /// <para>
        /// A null digest is <c>pbj.scenario-pull</c>'s deliberate override — "send
        /// me the combat save whatever I hold" — so it resolves to the slot. When
        /// nothing matches, the lobby's campaign wins if one is selected, because
        /// that is what a peer in a lobby is waiting on; the mismatch is logged by
        /// the caller either way, and answering with something keeps M9's rule that
        /// a request always makes progress.
        /// </para>
        /// </remarks>
        private ScenarioPayload ResolveRequested(string? digest)
        {
            var slot = bridge.ReadScenario(LobbySaveNames.ScenarioSlot);
            if (digest == null)
            {
                return slot;
            }
            if (slot.Inspect() == ScenarioRejection.None && slot.Matches(digest))
            {
                return slot;
            }

            var selected = selection.SaveKey;
            if (string.IsNullOrEmpty(selected)
                || LobbySaveNames.IsNonCampaignSlot(selected))
            {
                return slot;
            }
            return bridge.ReadScenario(selected);
        }
    }
}
