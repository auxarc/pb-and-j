using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Everything playback logs: keyframes, poses, and the M14 asset tracks --
    // trails, lights, reactions and melees -- including every count that gets
    // clamped, dropped or refused on the way, and the visibility corrections that
    // travel with them.
    // The middle of the `// --- orders and commit ---` banner's span, and the
    // reason that banner had to be divided at all.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // This class has no helpers and no fields -- every member is a test -- so
    // unlike the other split test classes there is no shared fixture in
    // NetLogTests.cs to look for.
    public partial class NetLogTests
    {
        [Fact]
        public void KeyframesSent_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 3 keyframes | 12 tracks, 640 keys | 15.00s-20.00s | broadcast to 1 peer",
                NetLog.KeyframesSent(3, 12, 640, 15f, 20f, 1));
        }

        [Fact]
        public void KeyframesReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 3 keyframes received | 12 tracks, 640 keys | 5.00s of motion",
                NetLog.KeyframesReceived(3, 12, 640, 15f, 20f));
        }

        // Not a warning: a scenario with prediction disabled records nothing, and
        // the snapshot still corrects everyone.
        [Fact]
        public void KeyframesUnavailable_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] no keyframes recorded this turn — snapshot correction only",
                NetLog.KeyframesUnavailable());
        }

        [Fact]
        public void KeyframesClamped_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] keyframes clamped: 130 tracks captured, only 128 fit; 4 track(s) thinned",
                NetLog.KeyframesClamped(130, 128, 4));
        }

        [Fact]
        public void PosesSent_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 poses | 8 unit tracks | broadcast to 1 peer",
                NetLog.PosesSent(4, 8, 1));
        }

        [Fact]
        public void PosesSent_SpeaksOfOneTrackAndOnePeerInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 poses | 1 unit track | broadcast to 2 peers",
                NetLog.PosesSent(4, 1, 2));
        }

        [Fact]
        public void PosesReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 poses complete | 8 unit tracks | playing the battle",
                NetLog.PosesReceived(4, 8));
        }

        // The one symptom a player can see and cannot explain is a turn that
        // slides instead of walking. "3 of 8" is the difference between a bug
        // report and a diagnosis, so this is logged every time rather than only
        // on the interesting arm.
        [Fact]
        public void PosesIncomplete_SaysWhatArrivedAndWhatItWillLookLike()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 poses incomplete — 3 of 8 arrived | units will slide, not walk",
                NetLog.PosesIncomplete(4, 3, 8));
        }

        [Fact]
        public void AssetsSent_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects | 130 tracks in 3 parts | broadcast to 2 peers",
                NetLog.AssetsSent(4, 3, 130, 2));
        }

        [Fact]
        public void AssetsSent_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects | 1 track in 1 part | broadcast to 1 peer",
                NetLog.AssetsSent(4, 1, 1, 1));
        }

        [Fact]
        public void AssetsReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects complete | 130 tracks | "
                    + "the battle will be shot as well as walked",
                NetLog.AssetsReceived(4, 130));
        }

        [Fact]
        public void AssetsIncomplete_SaysWhatArrivedAndWhatItWillLookLike()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects incomplete — 3 of 8 arrived | "
                    + "nothing will fire this turn",
                NetLog.AssetsIncomplete(4, 3, 8));
        }

        // Per-track and non-fatal, so the line names a count rather than a
        // demotion — and names one reason rather than claiming to be the whole
        // story, which the wording has to carry because the count can cover
        // several different faults.
        // The line that keeps the one above from crying wolf. A turn with no
        // effects is usually just a quiet turn, and a reader with no line at all
        // cannot tell that from a broken feature.
        [Fact]
        public void AssetsNoneSent_SeparatesAQuietTurnFromALostOne()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects: none sent — a quiet turn, or a host that recorded none",
                NetLog.AssetsNoneSent(4));
        }

        [Fact]
        public void AssetsDropped_NamesTheCountAndAReason()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects: 2 tracks dropped, one of them for TooFewKeys",
                NetLog.AssetsDropped(4, 2, AssetTrackFault.TooFewKeys));
        }

        [Fact]
        public void AssetsDropped_SpeaksOfOneTrackInTheSingular()
        {
            Assert.Contains("1 track dropped", NetLog.AssetsDropped(4, 1, AssetTrackFault.NoKey));
        }

        // Named because this is the one failure that says the two machines
        // disagree about their content — the handshake refuses a mismatched
        // build and mod version, but pools can still diverge at identical ones.
        [Fact]
        public void AssetUnplayable_NamesTheKeyAndTheReason()
        {
            Assert.Equal(
                "[pb-and-j] cannot show effect 'fx_muzzle_rifle': no such asset pool — "
                    + "it will be missing from this turn",
                NetLog.AssetUnplayable("fx_muzzle_rifle", "no such asset pool"));
        }

        [Fact]
        public void AssetUnplayable_WithNoKeyAtAll_SaysSo()
        {
            Assert.Contains("effect '(unnamed)'", NetLog.AssetUnplayable(null, "no such asset pool"));
        }

        // The successor to stage A's AssetTrailsNotCaptured. That line reported
        // these projectiles as a loss, fired on 3 of 109 in a real turn, and is
        // how stage B learned trails were worth building — so it is kept as a
        // count rather than deleted. Points per turn is what the trail cap is
        // sized against.
        [Fact]
        public void AssetTrailsSent_ReportsProjectilesAndPoints()
        {
            Assert.Equal(
                "[pb-and-j] 3 projectiles carried trails | 97 points",
                NetLog.AssetTrailsSent(3, 97, 0));
        }

        [Fact]
        public void AssetTrailsSent_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] 1 projectile carried trails | 1 point",
                NetLog.AssetTrailsSent(1, 1, 0));
        }

        // The cap was sized believing no real trail would reach it, and a
        // playtest measured ~68 points on an ordinary missile against a cap of
        // 64. Thinning is therefore the normal path, and at 68 it is invisible
        // while at 300 it would not be — so it has to say so.
        [Fact]
        public void AssetTrailsSent_WhenTheCapBit_SaysSoAndNamesIt()
        {
            Assert.Equal(
                "[pb-and-j] 3 projectiles carried trails | 205 points "
                    + "| 2 over the 64-point cap and thinned",
                NetLog.AssetTrailsSent(3, 205, 2));
        }

        // The positive counterpart to the two loss lines. Without it a run with
        // both losses at zero reads the same whether every flash travelled or
        // no light code ran at all.
        [Fact]
        public void AssetLightsSent_ReportsUnitsAndLights()
        {
            Assert.Equal(
                "[pb-and-j] 3 units fired 7 weapon lights",
                NetLog.AssetLightsSent(3, 7));
        }

        [Fact]
        public void AssetLightsSent_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] 1 unit fired 1 weapon light",
                NetLog.AssetLightsSent(1, 1));
        }

        [Fact]
        public void AssetReactionsAndMeleesSent_ReportsBoth()
        {
            Assert.Equal(
                "[pb-and-j] 4 reaction pings and 2 melee swings sent",
                NetLog.AssetReactionsAndMeleesSent(4, 2));
        }

        [Fact]
        public void AssetReactionsAndMeleesSent_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] 1 reaction ping and 1 melee swing sent",
                NetLog.AssetReactionsAndMeleesSent(1, 1));
        }

        [Fact]
        public void ReactionsAndMeleesPlayed_ReportsBoth()
        {
            Assert.Equal(
                "[pb-and-j] 4 reaction pings and 2 melee swings played",
                NetLog.ReactionsAndMeleesPlayed(4, 2));
        }

        [Fact]
        public void ReactionsAndMeleesPlayed_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] 1 reaction ping and 1 melee swing played",
                NetLog.ReactionsAndMeleesPlayed(1, 1));
        }

        [Fact]
        public void MeleesOverCap_NamesTheSliceAsTheSuspect()
        {
            // The cap is not the thing to raise when this fires. An unsliced
            // list grows for the whole fight and breaches any cap eventually,
            // so the message points at the slice instead.
            Assert.Equal(
                "[pb-and-j] dropped 3 melee swings over the per-unit cap — suspect the window slice",
                NetLog.MeleesOverCap(3));
        }

        [Fact]
        public void MeleesOverCap_SpeaksOfOneInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] dropped 1 melee swing over the per-unit cap — suspect the window slice",
                NetLog.MeleesOverCap(1));
        }

        // The one cost of hanging lights off the pose track, made loud. A unit
        // the recorder skipped drops its flashes with it, and that is invisible
        // on screen among other flashes.
        [Fact]
        public void LightsWithoutPoseTrack_NamesBothCounts()
        {
            Assert.Equal(
                "[pb-and-j] 2 units fired 5 weapon lights but carried no pose track — "
                    + "those flashes will not reach the client",
                NetLog.LightsWithoutPoseTrack(2, 5));
        }

        [Fact]
        public void LightsWithoutPoseTrack_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Contains("1 unit fired 1 weapon light ", NetLog.LightsWithoutPoseTrack(1, 1));
        }

        // Deliberately a different line from the one above: same symptom, wholly
        // unrelated cause, and a reader who saw them merged would chase the
        // wrong one.
        [Fact]
        public void LightsUnusable_ReportsTheSocketlessOnes()
        {
            Assert.Equal(
                "[pb-and-j] 4 weapon lights had no usable socket and will not travel",
                NetLog.LightsUnusable(4));
        }

        [Fact]
        public void LightsUnusable_SpeaksOfOneInTheSingular()
        {
            Assert.Contains("1 weapon light ", NetLog.LightsUnusable(1));
        }

        // The only place this loss can be reported at all: the client never gets
        // the terminator it would report against.
        [Fact]
        public void AssetsWithoutTracks_ExplainsTheOneLossTheClientCannotSee()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 recorded 12 effect tracks but no unit motion — "
                    + "none of it can be sent, because the keyframes that would end the burst "
                    + "are what is missing",
                NetLog.AssetsWithoutTracks(4, 12));
        }

        [Fact]
        public void AssetsWithoutTracks_SpeaksOfOneTrackInTheSingular()
        {
            Assert.Contains("1 effect track ", NetLog.AssetsWithoutTracks(4, 1));
        }

        [Fact]
        public void AssetsOverCapacity_SaysTheFightOutgrewTheCaps()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 effects past the per-turn cap — 5 tracks dropped | "
                    + "this fight is bigger than the caps were measured for",
                NetLog.AssetsOverCapacity(4, 5));
        }

        [Fact]
        public void AssetsOverCapacity_SpeaksOfOneTrackInTheSingular()
        {
            Assert.Contains("1 track dropped", NetLog.AssetsOverCapacity(4, 1));
        }

        [Fact]
        public void VisibilityCorrected_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] visibility corrected | 3 units revealed, 1 hidden",
                NetLog.VisibilityCorrected(3, 1));
        }

        [Fact]
        public void VisibilityCorrected_SpeaksOfOneRevealedUnitInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] visibility corrected | 1 unit revealed, 0 hidden",
                NetLog.VisibilityCorrected(1, 0));
        }

        [Fact]
        public void PosesNotCaptured_NamesBothLossesSeparately()
        {
            Assert.Equal(
                "[pb-and-j] poses partly uncaptured: 2 units without recorded bones, "
                    + "7 keys whose skeleton no longer matches",
                NetLog.PosesNotCaptured(2, 7));
        }

        [Fact]
        public void PosesNotCaptured_SpeaksOfOneOfEachInTheSingular()
        {
            Assert.Equal(
                "[pb-and-j] poses partly uncaptured: 1 unit without recorded bones, "
                    + "1 key whose skeleton no longer matches",
                NetLog.PosesNotCaptured(1, 1));
        }

        [Fact]
        public void PosesUnsendable_NamesTheFaultAndTheUnitThatCausedIt()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 poses dropped: Ragged on 'pb_mech_01' — "
                    + "the whole turn plays transform-only",
                NetLog.PosesUnsendable(4, PoseTrackFault.Ragged, "pb_mech_01"));
        }

        [Fact]
        public void PosesUnsendable_WithAnUnnamedUnit_SaysSo()
        {
            Assert.Contains(
                "on '(unnamed)'", NetLog.PosesUnsendable(4, PoseTrackFault.NameTooLong, null));
        }
    }
}
