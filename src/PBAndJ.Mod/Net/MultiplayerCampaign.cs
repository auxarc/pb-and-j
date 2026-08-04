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

        internal static void Enter(string saveKey)
        {
            Active = true;
            SaveKey = saveKey;
            Debug.Log("[pb-and-j] this campaign is multiplayer — saves will stay in the "
                + LobbySaveNames.Prefix + " namespace");
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
