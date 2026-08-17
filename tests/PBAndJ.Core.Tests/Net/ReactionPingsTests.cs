using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ReactionPingsTests
    {
        private static readonly float[] None = new float[0];

        [Fact]
        public void Latest_OfAnEmptyList_IsNothing()
        {
            // Nothing, not zero. Zero is a real stamp on the host's clock, and
            // handing it to OnReactionPing would be a claim rather than a
            // silence.
            Assert.Null(ReactionPings.LatestAtOrBefore(None, 5f));
        }

        [Fact]
        public void Latest_OfANullList_IsNothing()
        {
            Assert.Null(ReactionPings.LatestAtOrBefore(null, 5f));
        }

        [Fact]
        public void Latest_BeforeTheFirstPing_IsNothing()
        {
            // ⚠️ Deliberately NOT the clamp-to-first behaviour of
            // KeyframePlayback.TrySample. A pose has to be somewhere at every
            // instant; a ping that has not happened yet has not happened.
            Assert.Null(ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, 1.9f));
        }

        [Fact]
        public void Latest_TakesTheLastPingAtOrBeforeTheCursor()
        {
            Assert.Equal(2f, ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, 3f));
            Assert.Equal(4f, ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, 4.5f));
        }

        [Fact]
        public void Latest_CountsAPingExactlyOnTheCursor()
        {
            // The game's test is `!(time > requested)`, so equality counts. An
            // exclusive test loses a ping stamped on a frame boundary, which is
            // where a ping recorded by the same clock most often lands.
            Assert.Equal(4f, ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, 4f));
        }

        [Fact]
        public void Latest_PastTheEnd_IsTheFinalPing()
        {
            Assert.Equal(4f, ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, 99f));
        }

        [Fact]
        public void Latest_ScansBackwards_SoOnlyTheNewestWins()
        {
            Assert.Equal(6f, ReactionPings.LatestAtOrBefore(new[] { 2f, 4f, 6f }, 7f));
        }

        [Fact]
        public void Latest_OfANaNCursor_IsNothing()
        {
            // `!(time > NaN)` is true for every key, so a naive transcription
            // returns the LAST ping for a NaN cursor — arming a glow off a
            // hostile float. Guarded rather than inherited.
            Assert.Null(ReactionPings.LatestAtOrBefore(new[] { 2f, 4f }, float.NaN));
        }

        [Fact]
        public void Latest_SkipsANaNStamp()
        {
            // A NaN stamp fails `time <= cursor`, so the scan keeps going and
            // finds the real ping behind it rather than stopping on the bad one.
            Assert.Equal(2f, ReactionPings.LatestAtOrBefore(new[] { 2f, float.NaN }, 5f));
        }
    }
}
