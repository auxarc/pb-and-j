using System;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Whether the campaign currently loaded is a multiplayer one.
    /// </summary>
    /// <remarks>
    /// M11b's <c>pbj_</c> namespace holds on <em>reads</em> only. The moment a
    /// co-op campaign is actually loaded, the game starts writing it back out
    /// under unprefixed names of its own choosing, and the title screen's
    /// Continue would later offer it as singleplayer. This bit is what a save
    /// patch consults to stop that.
    /// <para>
    /// <b>Set from the load's success callback, never before the load.</b> If it
    /// were set up front and the load then failed — silently, as it can — the
    /// player would still be in their singleplayer campaign with every
    /// subsequent save being prefixed and therefore hidden from the load screen
    /// and from Continue. Their campaign would look deleted.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class MultiplayerCampaign
    {
        internal static bool Active { get; private set; }

        internal static string? SaveKey { get; private set; }

        /// <remarks>
        /// <b>Two callers reach this on one load, and both are correct.</b>
        /// M11d's <c>LoadGlue</c> callback fires for a lobby-driven load;
        /// M11e's <c>LoadingEnd2</c> postfix fires for a game-driven one such as
        /// combat retry, which never goes through <c>LoadGlue</c> at all. The
        /// lobby path trips both. Setting the same values twice is harmless, but
        /// announcing it twice is not — a duplicated line reads as a bug in the
        /// log, and it was reported as one from the first two-party session
        /// (2026-08-07). So the announcement is a TRANSITION, matching
        /// <see cref="Leave"/>, rather than an echo of every call. Entering a
        /// different campaign still announces, because that genuinely is one.
        /// </remarks>
        internal static void Enter(string saveKey)
        {
            var changed = !Active || !string.Equals(SaveKey, saveKey, StringComparison.Ordinal);
            Active = true;
            SaveKey = saveKey;
            if (changed)
            {
                Debug.Log("[pb-and-j] this campaign is multiplayer — saves will stay in the "
                    + LobbySaveNames.Prefix + " namespace");
            }
        }

        /// <summary>
        /// Left the campaign. Must run on every exit, or a later singleplayer
        /// session writes prefixed saves.
        /// </summary>
        internal static void Leave()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            SaveKey = null;
            Debug.Log("[pb-and-j] left the multiplayer campaign");
        }
    }
}
