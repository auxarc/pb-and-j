namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Which seat this machine is in.
    /// </summary>
    /// <remarks>
    /// <see cref="Solo"/> is not the absence of a role, it is a role: a
    /// singleplayer game drives its own overworld and must keep doing so. The
    /// mod is installed for everyone who has it, session or not, so "no session"
    /// is the common case and the rules have to say something correct about it.
    /// </remarks>
    public enum SessionRole : byte
    {
        /// <summary>No session. An ordinary singleplayer game.</summary>
        Solo = 0,

        /// <summary>Hosting. This machine drives the overworld for everyone.</summary>
        Host = 1,

        /// <summary>Joined someone else's session. A passenger.</summary>
        Client = 2,
    }

    /// <summary>
    /// The ways a player can drive the world from the overworld screens.
    /// </summary>
    /// <remarks>
    /// This list is the whole point of the type, and it is longer than the first
    /// draft of M12a assumed. That draft said base control had a single funnel —
    /// <c>OverworldUtility.OrderMovementToPosition</c> — and that suppressing it
    /// suppressed everything, with the time-scale buttons already inert because
    /// <c>RefreshTimeScale</c> derives from <c>isBaseMoving</c>. The 2026-08-08
    /// review refuted that, and each of the other three arms below is a measured
    /// route by which a passenger could drive a world it does not own:
    /// <list type="bullet">
    ///   <item><see cref="Camp"/> and <see cref="Retreat"/> start a
    ///   <c>SimulationLockCountdown</c>, and <c>RefreshTimeScale</c> skips its
    ///   <c>isBaseMoving</c> derivation entirely whenever a lock exists — so the
    ///   shared clock runs with no movement order in sight. Retreat also
    ///   relocates the base through <c>SimulationLockReposition</c>.</item>
    ///   <item><see cref="EngageSite"/> reaches <c>ScenarioSetupUtility.EnterCombat</c>
    ///   through the client's own site-interaction dialog, once the mirror has
    ///   put its base in range of something.</item>
    /// </list>
    /// There is a fourth consequence that is easy to miss: a client's sim lock
    /// <em>ending</em> wipes <c>world_gen_visited_</c> and calls
    /// <c>RefreshStandaloneGeneratorEncounters</c>, re-rolling every generated
    /// contract. So suppressing <see cref="Camp"/> is not politeness about the
    /// clock — it is what stops one click undoing M12b.
    /// </remarks>
    public enum OverworldControl : byte
    {
        /// <summary>Ordering the mobile base to a position on the map.</summary>
        MoveBase = 1,

        /// <summary>Making camp, which runs the shared clock forward.</summary>
        Camp = 2,

        /// <summary>Retreating to a resupply base, which also moves the base.</summary>
        Retreat = 3,

        /// <summary>Starting a mission at a site.</summary>
        EngageSite = 4,
    }

    /// <summary>
    /// Who is allowed to drive the world.
    /// </summary>
    /// <remarks>
    /// The authority split in one place: the host drives the overworld base and
    /// the campaign clock, everything else out of combat is concurrent. These
    /// live in Core rather than in the Harmony patches because they are a
    /// decision, and the glue is not measured — the same reasoning that moved
    /// the non-loopback passphrase rule out of <c>NetGlue</c> and into
    /// <c>ConnectRules</c>.
    /// </remarks>
    public static class PassengerRules
    {
        /// <summary>
        /// Whether this machine may perform <paramref name="control"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately not parameterised by which control a host may use: a
        /// host and a solo game may use all of them, and inventing a
        /// per-control host rule now would be a branch nothing can reach, which
        /// the 100% gate turns into a build failure rather than dead code.
        /// </remarks>
        public static bool Allows(SessionRole role, OverworldControl control)
        {
            // Fail closed on anything unrecognised. A role we cannot identify is
            // not evidence of authority, and the cost of guessing wrong is a
            // silently desynchronised world — worse than a refused click.
            _ = control;
            return role == SessionRole.Solo || role == SessionRole.Host;
        }
    }

    /// <summary>
    /// What a passenger is told when the world refuses them.
    /// </summary>
    /// <remarks>
    /// Screen voice, not log voice — the same split <c>ConnectText</c> and
    /// <c>NetLog</c> already keep. Every sentence names the host, because a
    /// refusal that does not say whose authority it is reads as a broken
    /// button; that is the connect screen's lesson, where a working connection
    /// and a silent failure looked identical to the person clicking.
    /// <para>
    /// The wording is per control rather than per reason. A player who clicked
    /// Camp is thinking about rest, not about who drives, so answering "the host
    /// drives the base" would be true of the mechanism and useless as an
    /// explanation.
    /// </para>
    /// </remarks>
    public static class PassengerText
    {
        /// <summary>The sentence to show when <paramref name="control"/> is refused.</summary>
        public static string Refusal(OverworldControl control)
        {
            switch (control)
            {
                case OverworldControl.MoveBase:
                    return "The host drives the base.";
                case OverworldControl.Camp:
                    return "Only the host can make camp — it runs the clock everyone shares.";
                case OverworldControl.Retreat:
                    return "Only the host can retreat, because it moves the base everyone is in.";
                case OverworldControl.EngageSite:
                    return "The host starts missions.";
                default:
                    // A control added to the enum without a sentence here would
                    // otherwise reach a live screen as an empty tooltip, which
                    // is the silent refusal this whole type exists to prevent.
                    return "The host controls this.";
            }
        }
    }
}
