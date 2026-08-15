using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class AssetBufferTests
    {
        private static StandaloneAssetTrack Standalone(int id) =>
            new StandaloneAssetTrack(
                id, new AssetTrackHead("fx_impact", 0f, 1f), new Vec3(0f, 0f, 0f),
                new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 0f),
                new Vec3(0f, 0f, 0f));

        private static ProjectileAssetTrack Projectile(int id) =>
            new ProjectileAssetTrack(
                id, new AssetTrackHead("fx_bullet", 0f, 1f), new Vec3(1f, 1f, 1f),
                new[]
                {
                    new TransformKey(0f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(1f, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                });

        private static BeamAssetTrack Beam(int id) =>
            new BeamAssetTrack(
                id, new AssetTrackHead("fx_beam", 0f, 1f),
                new[]
                {
                    new BeamKey(0f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f), new Vec3(0f, 0f, 0f)),
                    new BeamKey(1f, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f), new Vec3(0f, 0f, 1f)),
                });

        private static ReplayAssetsMessage Part(int turn, int index, int count, AssetCapture? assets) =>
            new ReplayAssetsMessage(turn, index, count, assets);

        [Fact]
        public void Empty_ReportsNoTurn()
        {
            var buffer = new AssetBuffer();

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsHeld);
            Assert.Equal(0, buffer.PartsExpected);
        }

        [Fact]
        public void Take_WithEveryPart_ReassemblesAllThreeKindsInOrder()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 2, new AssetCapture(
                new[] { Standalone(1), Standalone(2) }, new[] { Projectile(3) }, null)));
            buffer.Accept(Part(4, 1, 2, new AssetCapture(
                new[] { Standalone(5) }, new[] { Projectile(6) }, new[] { Beam(7) })));

            Assert.Equal(4, buffer.Turn);
            Assert.Equal(2, buffer.PartsHeld);
            Assert.Equal(2, buffer.PartsExpected);

            var taken = buffer.Take(4);

            Assert.Equal(new[] { 1, 2, 5 }, new[]
            {
                taken.Standalone[0].Id, taken.Standalone[1].Id, taken.Standalone[2].Id,
            });
            Assert.Equal(new[] { 3, 6 }, new[] { taken.Projectiles[0].Id, taken.Projectiles[1].Id });
            Assert.Equal(7, Assert.Single(taken.Beams).Id);
        }

        [Fact]
        public void Take_WithAPartMissing_ReturnsNothing()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 3, new AssetCapture(new[] { Standalone(1) }, null, null)));

            Assert.True(buffer.Take(4).IsEmpty);
        }

        [Fact]
        public void Take_ForAnotherTurn_ReturnsNothing()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 1, new AssetCapture(new[] { Standalone(1) }, null, null)));

            Assert.True(buffer.Take(5).IsEmpty);
        }

        // The terminator for a turn is the last thing that will ever refer to
        // it, so anything still held afterwards is unreachable by definition and
        // could only confuse a later turn.
        [Fact]
        public void Take_EmptiesTheBufferEvenWhenItFails()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 3, new AssetCapture(new[] { Standalone(1) }, null, null)));
            buffer.Take(4);

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsHeld);
            Assert.Equal(0, buffer.PartsExpected);
        }

        [Fact]
        public void Take_WhenCompleteButEmpty_ReturnsNothing()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 1, AssetCapture.None));

            Assert.True(buffer.Take(4).IsEmpty);
        }

        // The newest thing the host said is the thing to believe: a client that
        // rejoins mid-combat can be told about a turn number it has already
        // seen, and treating the newer message as stale would leave it
        // accumulating a turn that will never complete.
        [Fact]
        public void Accept_APartForAnotherTurn_StartsOver()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 2, new AssetCapture(new[] { Standalone(1) }, null, null)));
            buffer.Accept(Part(2, 0, 1, new AssetCapture(new[] { Standalone(9) }, null, null)));

            Assert.Equal(2, buffer.Turn);
            var taken = buffer.Take(2);
            Assert.Equal(9, Assert.Single(taken.Standalone).Id);
        }

        [Fact]
        public void Accept_TheSamePartTwice_CountsItOnce()
        {
            var buffer = new AssetBuffer();
            var part = Part(4, 0, 2, new AssetCapture(new[] { Standalone(1) }, null, null));
            buffer.Accept(part);
            buffer.Accept(part);

            Assert.Equal(1, buffer.PartsHeld);
            Assert.True(buffer.Take(4).IsEmpty);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void Accept_APartIndexOutsideTheCount_IsIgnored(int index)
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, index, 2, new AssetCapture(new[] { Standalone(1) }, null, null)));

            Assert.Equal(0, buffer.PartsHeld);
        }

        [Fact]
        public void Clear_ForgetsEverything()
        {
            var buffer = new AssetBuffer();
            buffer.Accept(Part(4, 0, 2, new AssetCapture(new[] { Standalone(1) }, null, null)));
            buffer.Clear();

            Assert.Equal(-1, buffer.Turn);
            Assert.Equal(0, buffer.PartsHeld);
        }
    }
}
