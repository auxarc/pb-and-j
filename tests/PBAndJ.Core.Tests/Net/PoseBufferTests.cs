using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PoseBufferTests
    {
        private static UnitPoseTrack Track(string name) =>
            new UnitPoseTrack(name, new[] { "j" }, null);

        private static PosesMessage Part(int turn, int index, int count, string? name = "u") =>
            new PosesMessage(turn, index, count, name == null ? null : Track(name));

        [Fact]
        public void Empty_ReportsNoTurn()
        {
            var buffer = new PoseBuffer();

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsHeld);
            Assert.Equal(0, buffer.PartsExpected);
        }

        [Fact]
        public void Take_WithEveryPart_ReturnsThemAll()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 2, "a"));
            buffer.Accept(Part(4, 1, 2, "b"));

            Assert.Equal(4, buffer.Turn);
            Assert.Equal(2, buffer.PartsHeld);
            Assert.Equal(2, buffer.PartsExpected);

            var taken = buffer.Take(4);

            Assert.Equal(new[] { "a", "b" }, new[] { taken[0].Name, taken[1].Name });
        }

        [Fact]
        public void Take_WithAPartMissing_ReturnsNothing()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 3));

            Assert.Empty(buffer.Take(4));
        }

        // The terminator for a turn is the last thing that will ever refer to
        // it, so anything still held afterwards is unreachable and could only
        // confuse a later turn.
        [Fact]
        public void Take_EmptiesTheBufferEvenWhenItFails()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 3));
            buffer.Take(4);

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsHeld);
        }

        [Fact]
        public void Take_ForADifferentTurnThanIsHeld_ReturnsNothing()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 1));

            Assert.Empty(buffer.Take(5));
        }

        [Fact]
        public void Accept_APartForANewTurn_StartsOver()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 2, "stale"));
            buffer.Accept(Part(5, 0, 1, "fresh"));

            var taken = buffer.Take(5);

            Assert.Equal("fresh", Assert.Single(taken).Name);
        }

        // Believing the newest label rather than the highest one: a client that
        // rejoins mid-combat can be told about a turn it has already seen, and
        // treating that as stale would leave it accumulating one that will never
        // complete.
        [Fact]
        public void Accept_APartForAnEarlierTurn_StillStartsOver()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(9, 0, 1, "later"));
            buffer.Accept(Part(2, 0, 1, "earlier"));

            Assert.Equal("earlier", Assert.Single(buffer.Take(2)).Name);
        }

        [Fact]
        public void Accept_TheSamePartTwice_CountsItOnce()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 2, "a"));
            buffer.Accept(Part(4, 0, 2, "a"));

            Assert.Equal(1, buffer.PartsHeld);
            Assert.Empty(buffer.Take(4));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void Accept_APartIndexOutsideWhatWasPromised_IsIgnored(int index)
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, index, 2));

            Assert.Equal(0, buffer.PartsHeld);
        }

        [Fact]
        public void Accept_APartCarryingNoTrack_StillCountsTowardsCompleteness()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 2, null));
            buffer.Accept(Part(4, 1, 2, "b"));

            Assert.Equal(2, buffer.PartsHeld);
            Assert.Equal("b", Assert.Single(buffer.Take(4)).Name);
        }

        [Fact]
        public void Take_WhenNoPartsWerePromised_ReturnsNothing()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 0));

            Assert.Empty(buffer.Take(4));
        }

        [Fact]
        public void Clear_ForgetsEverything()
        {
            var buffer = new PoseBuffer();
            buffer.Accept(Part(4, 0, 1));
            buffer.Clear();

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsExpected);
            Assert.Empty(buffer.Take(4));
        }
    }
}
