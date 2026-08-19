using System.Globalization;

namespace PBAndJ.Core.Net
{
    // M14's replayed effects: the assets shipped for a fight, and every way the set
    // can disappoint -- incomplete, dropped, over cap, or unplayable when it lands.
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
        // --- replayed effects (M14) ---

        public static string AssetsSent(
            int turn, int partCount, int trackCount, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects | {1} track{2} in {3} part{4} | broadcast to {5} peer{6}",
                turn, trackCount, Plural(trackCount), partCount, Plural(partCount),
                peerCount, Plural(peerCount));
        }

        /// <summary>
        /// The turn plays with its projectiles, beams and impacts.
        /// </summary>
        public static string AssetsReceived(int turn, int trackCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects complete | {1} track{2} | the battle will be shot as well as walked",
                turn, trackCount, Plural(trackCount));
        }

        /// <summary>
        /// The host announced effects and not all of them arrived.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="AssetsNoneSent"/>, which is the deliberate
        /// difference from the pose pair above. A turn with no poses always
        /// means the units will slide, so <see cref="PosesIncomplete"/> is
        /// truthful on every turn it fires. A turn with no <i>effects</i> is
        /// usually just a quiet turn — the measured fight's first turn had no
        /// contact at all — so reporting one as incomplete would cry wolf on
        /// ordinary play and teach the reader to skip the line that matters.
        /// </remarks>
        public static string AssetsIncomplete(int turn, int held, int expected)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects incomplete — {1} of {2} arrived | nothing will fire this turn",
                turn, held, expected);
        }

        /// <summary>
        /// The host sent no effects for this turn at all.
        /// </summary>
        /// <remarks>
        /// Said rather than left silent, because "nothing shoots on the client"
        /// is the symptom a player can see and cannot explain, and this line is
        /// what separates its two causes: a quiet turn, or a host that recorded
        /// nothing. Both are ordinary; neither is a defect; and a reader with no
        /// line at all cannot tell either of them from a broken feature.
        /// </remarks>
        public static string AssetsNoneSent(int turn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects: none sent — a quiet turn, or a host that recorded none",
                turn);
        }

        /// <summary>
        /// Tracks the host captured but could not put on the wire.
        /// </summary>
        /// <remarks>
        /// Per-track and non-fatal, unlike <see cref="PosesUnsendable"/>: one
        /// absent effect among a turn's effects is invisible, so these are
        /// dropped individually rather than demoting the turn. That is exactly
        /// why the count is said out loud — an invisible loss with no line in
        /// the log is a loss nobody can ever investigate.
        /// <para>
        /// <paramref name="reason"/> is <i>a</i> reason and not <i>the</i>
        /// reason: the count can cover several faults and the line names the
        /// last one seen. Naming one cheaply is worth more than either
        /// tallying all of them or picking a "worst" by an ordering the enum
        /// does not actually have.
        /// </para>
        /// </remarks>
        public static string AssetsDropped(int turn, int dropped, AssetTrackFault reason)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects: {1} track{2} dropped, one of them for {3}",
                turn, dropped, Plural(dropped), reason);
        }

        /// <summary>
        /// The turn held more effects than the wire format admits.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="AssetsDropped"/> because it means something
        /// different: not "these tracks were malformed" but "this fight is
        /// larger than anything the caps were measured against". The right
        /// response is to raise the caps, and that decision needs to know it is
        /// being asked for.
        /// </remarks>
        public static string AssetsOverCapacity(int turn, int dropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} effects past the per-turn cap — {1} track{2} dropped | "
                    + "this fight is bigger than the caps were measured for",
                turn, dropped, Plural(dropped));
        }

        /// <summary>
        /// One effect the client cannot show, said once and then never again.
        /// </summary>
        /// <remarks>
        /// Once is the whole point. Vanilla re-attempts activation for an
        /// unassigned active track on <b>every frame</b>, and an unresolvable
        /// key makes <c>AssetPoolUtility.IsInstanceAvailable</c> log on every
        /// one of those — so a single bad key is a warning per frame for the
        /// length of its window. The track is abandoned after this line.
        /// <para>
        /// The key is named because this is the one failure that says the two
        /// machines disagree about their content: the handshake refuses a
        /// mismatched game build and mod version, but DLC or workshop pools can
        /// still diverge at identical versions, and the key is what identifies
        /// which.
        /// </para>
        /// </remarks>
        public static string AssetUnplayable(string? assetKey, string why)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "cannot show effect '{0}': {1} — it will be missing from this turn",
                assetKey ?? "(unnamed)", why);
        }

        /// <summary>
        /// Projectiles crossed carrying trails, and how many points they cost.
        /// </summary>
        /// <remarks>
        /// The successor to stage A's <c>AssetTrailsNotCaptured</c>, which
        /// reported the same projectiles as a <i>loss</i> because trails did not
        /// travel yet. That line was written to catch the moment a weapon
        /// producing trails reached the field, and it did exactly that — it
        /// fired on 3 of 109 projectiles in a real turn, which is how stage B
        /// learned trails were worth building and cheap enough to build. Kept as
        /// a count rather than deleted, because points-per-turn is the number
        /// the trail cap is sized against and it is the only one that would show
        /// a weapon far heavier than anything measured.
        /// </remarks>
        public static string AssetTrailsSent(int projectiles, int points, int overCap)
        {
            var line = Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} projectile{1} carried trails | {2} point{3}",
                projectiles, Plural(projectiles), points, Plural(points));

            // Silent thinning is the failure this argument exists to prevent.
            // The cap was sized believing no real trail would reach it; a
            // playtest measured ~68 points on an ordinary missile against a cap
            // of 64, so it fires in normal play. At 68 the coarsening is
            // invisible and at 300 it would not be, and nothing else in the
            // system distinguishes those two.
            if (overCap > 0)
            {
                line += string.Format(
                    CultureInfo.InvariantCulture,
                    " | {0} over the {1}-point cap and thinned",
                    overCap, PbjMessageCodec.MaxTrailPointsPerTrack);
            }
            return line;
        }

        /// <summary>
        /// Weapon lights captured and put on the wire this turn.
        /// </summary>
        /// <remarks>
        /// The positive counterpart to <see cref="LightsWithoutPoseTrack"/> and
        /// <see cref="LightsUnusable"/>, and it exists because those two alone
        /// were not falsifiable. A playtest with both reading zero is equally
        /// consistent with "every flash travelled" and with "no light code ran
        /// at all" — the silent-success shape this project has now paid for
        /// several times over. A count that rises when weapons fire tells the
        /// two apart.
        /// </remarks>
        public static string AssetLightsSent(int units, int lights)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} unit{1} fired {2} weapon light{3}",
                units, Plural(units), lights, Plural(lights));
        }

        /// <summary>
        /// Reaction pings and melee swings leaving the host. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Positive counters, for the reason stage B had to learn twice: a wall
        /// of zeroed loss counters reads the same whether everything travelled
        /// or the capture never ran.
        /// </remarks>
        public static string AssetReactionsAndMeleesSent(int reactions, int melees)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} reaction ping{1} and {2} melee swing{3} sent",
                reactions, Plural(reactions), melees, Plural(melees));
        }

        /// <summary>
        /// Swings dropped because one unit carried more than the cap. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Should never fire: a unit gets one melee action per turn in practice
        /// and the capture slices to the turn's window before capping. If it
        /// does fire, the window slice is the thing to suspect, not the cap —
        /// an unsliced list accumulates for the whole fight and would breach any
        /// cap eventually.
        /// </remarks>
        public static string MeleesOverCap(int dropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "dropped {0} melee swing{1} over the per-unit cap — suspect the window slice",
                dropped, Plural(dropped));
        }

        /// <summary>
        /// What the client actually drove. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Counted at the window edge rather than per call: the newest ping is
        /// re-stamped every frame, so counting calls would count frames. The
        /// same shape stage B's <c>lightsFired</c> uses.
        /// </remarks>
        public static string ReactionsAndMeleesPlayed(int reactions, int melees)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} reaction ping{1} and {2} melee swing{3} played",
                reactions, Plural(reactions), melees, Plural(melees));
        }

        /// <summary>
        /// A unit fired but got no pose track, so its weapon lights have no ride.
        /// </summary>
        /// <remarks>
        /// The one cost of hanging lights off the pose track, made loud instead
        /// of silent. The join is deliberate — a light is meaningless without
        /// the unit whose <c>UnitLightManager</c> owns the <c>Light</c> — but it
        /// means a unit the recorder skipped, or one whose track failed
        /// <see cref="PoseTracks.TryPrepare"/>, drops its flashes with it. That
        /// is invisible on screen among other flashes, which is exactly the
        /// class of loss this project has learned to instrument rather than
        /// discover later.
        /// </remarks>
        public static string LightsWithoutPoseTrack(int units, int lights)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} unit{1} fired {2} weapon light{3} but carried no pose track — "
                    + "those flashes will not reach the client",
                units, Plural(units), lights, Plural(lights));
        }

        /// <summary>
        /// Weapon lights that could not travel, having no usable socket.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="LightsWithoutPoseTrack"/> because the causes
        /// are unrelated and the fixes are too: this one means the host recorded
        /// a flash whose mount socket is missing or absurd, so the client could
        /// never find the <c>Light</c> to drive even if it arrived.
        /// </remarks>
        public static string LightsUnusable(int lights)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "{0} weapon light{1} had no usable socket and will not travel",
                lights, Plural(lights));
        }

        /// <summary>
        /// A turn recorded effects but no unit tracks, so none of it can go.
        /// </summary>
        /// <remarks>
        /// Effects ride inside the host's "this turn recorded motion" guard,
        /// because the transform keyframes are what terminate them and parts
        /// nothing terminates are the one shape a client cannot resolve. That
        /// guard was priced for poses, where its cost is genuinely zero — a turn
        /// with no unit tracks has no units to pose.
        /// <para>
        /// For effects the cost is not zero, which is why this line exists.
        /// Capture drops destroyed units, so a mutual-destruction final volley
        /// records a turn full of explosions with no surviving unit to carry a
        /// track — the fight's climax, discarded. The client cannot report it
        /// either, since it never receives the terminator it would report
        /// against, so this is the only place it can be said at all.
        /// </para>
        /// </remarks>
        public static string AssetsWithoutTracks(int turn, int trackCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} recorded {1} effect track{2} but no unit motion — none of it can be sent, "
                    + "because the keyframes that would end the burst are what is missing",
                turn, trackCount, Plural(trackCount));
        }
    }
}
