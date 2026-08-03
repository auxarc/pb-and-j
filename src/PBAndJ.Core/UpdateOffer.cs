namespace PBAndJ.Core
{
    /// <summary>What, if anything, to put in front of the player.</summary>
    public enum UpdateOffer : byte
    {
        /// <summary>Say nothing. The log line has already been written either way.</summary>
        Nothing = 0,

        /// <summary>
        /// Offer to open the releases page, where the build can be downloaded
        /// and dropped in by hand.
        /// </summary>
        PointAtReleasePage = 1,
    }

    /// <summary>
    /// Decides whether being out of date is worth interrupting somebody over.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="UpdateCheck"/>: that answers "is
    /// this build stale", which is a fact, while this answers "should a modal
    /// appear", which is a manner. Only the second one can be irritating, and
    /// keeping it here means the rule is tested rather than buried in the glue
    /// alongside a Unity call nobody can run in a test.
    ///
    /// This mod cannot install anything — the game loads mod assemblies once, at
    /// startup, with no way to unload them — so the most it can honestly do is
    /// say where the build lives and let the player replace the folder. The page
    /// it opens is a compile-time constant in the glue, never a URL taken from
    /// GitHub's reply, which is why nothing here has to reason about whether a
    /// link can be trusted.
    /// </remarks>
    public static class UpdatePrompt
    {
        /// <param name="result">What the version comparison concluded.</param>
        /// <param name="dialogAvailable">
        /// Whether the game's confirmation view exists yet. It is a scene
        /// singleton, so early in startup there is simply nothing to open.
        /// </param>
        /// <param name="alreadyOffered">
        /// Whether this process has already asked once.
        /// </param>
        public static UpdateOffer Decide(UpdateResult result, bool dialogAvailable, bool alreadyOffered)
        {
            // Status first, and on its own. Everything else here is a reason not
            // to show a prompt that would otherwise be warranted; only this
            // decides whether one is warranted at all. Ordering them the other
            // way round would let a future edit start prompting people who are
            // already up to date.
            if (result.Status != UpdateStatus.UpdateAvailable)
            {
                return UpdateOffer.Nothing;
            }

            if (!dialogAvailable || alreadyOffered)
            {
                return UpdateOffer.Nothing;
            }

            return UpdateOffer.PointAtReleasePage;
        }
    }
}
