using System;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// The write side of the <c>pbj_</c> namespace: what name a save is actually
    /// stored under while a co-op campaign is loaded, and which names the
    /// singleplayer save screen must refuse.
    /// </summary>
    /// <remarks>
    /// M11b enforced the namespace on <em>reads</em> only. M11d loads one of these
    /// saves for real, and from that moment the game writes the campaign back out
    /// under names of its own choosing — <c>autosave_timed_N</c> from the overworld
    /// timer, <c>autosave_game_exit</c> on quit, and whatever the player types on
    /// the save screen. Every one of those would land outside the namespace the
    /// lobby selects from, and Continue would then offer a co-op campaign as
    /// singleplayer.
    /// <para>
    /// This is a data-safety decision, so it lives here under the coverage gate
    /// rather than in the Harmony patch that consults it — the same move that put
    /// the non-loopback-requires-passphrase rule in <see cref="ConnectRules"/>.
    /// Nothing here knows a game type: the patch answers "is this the normal save
    /// location" and "is a co-op campaign loaded", and the policy is decided here.
    /// </para>
    /// </remarks>
    public static class LobbySaveWrites
    {
        /// <summary>
        /// The name a save must actually be written under.
        /// </summary>
        /// <param name="saveName">The name the game chose.</param>
        /// <param name="multiplayerCampaign">
        /// Whether the campaign currently loaded is a multiplayer one. <b>Set from
        /// a load's success callback only</b> — a bit set before a load that then
        /// failed would prefix every save of the player's <em>singleplayer</em>
        /// campaign, hiding it from the load screen and from Continue.
        /// </param>
        /// <param name="normalLocation">
        /// Whether this is <c>SaveLocation.Normal</c>. The crash reporter writes
        /// under <c>SaveLocation.Reporter</c> and must not be touched.
        /// </param>
        /// <remarks>
        /// An already-prefixed name is returned unchanged, and that guard is
        /// load-bearing rather than tidy: <c>DoSave</c> re-enters itself through
        /// <c>DelayedSaveIE</c>, which the after-combat and after-stop autosaves
        /// both take, so prefixing unconditionally writes <c>pbj_pbj_…</c>.
        /// </remarks>
        public static string? NameForWrite(string? saveName, bool multiplayerCampaign, bool normalLocation)
        {
            if (string.IsNullOrEmpty(saveName)
                || !multiplayerCampaign
                || !normalLocation
                || LobbySaveNames.IsMultiplayerKey(saveName))
            {
                return saveName;
            }
            return LobbySaveNames.KeyFor(saveName);
        }

        /// <summary>
        /// Whether the singleplayer save screen must refuse to write
        /// <paramref name="key"/>.
        /// </summary>
        /// <remarks>
        /// Two rules in one answer. <see cref="LobbySaveNames.ScenarioSlot"/> is
        /// always refused: M9's transfer deletes and rewrites that directory
        /// wholesale, so it is inside the prefix without being a campaign. Every
        /// other multiplayer save is refused only from <em>outside</em> a co-op
        /// campaign — inside one, the namespace is the player's own, and refusing
        /// it there would leave retyping the name as the only overwrite route,
        /// which is precisely the route that skips the overwrite confirmation.
        /// <para>
        /// Deleting is deliberately not softened alongside this. Overwriting your
        /// own co-op save replaces your own campaign; deleting it destroys a save
        /// other peers have readied on.
        /// </para>
        /// </remarks>
        public static bool IsProtectedFromOverwrite(string? key, bool multiplayerCampaign)
        {
            if (string.Equals(key, LobbySaveNames.ScenarioSlot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return !multiplayerCampaign && LobbySaveNames.IsMultiplayerKey(key);
        }
    }
}
