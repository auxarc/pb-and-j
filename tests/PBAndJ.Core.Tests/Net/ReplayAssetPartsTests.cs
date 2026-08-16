using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ReplayAssetPartsTests
    {
        private static AssetTrackHead Head(string? key = "fx_impact", float start = 1f, float end = 2f) =>
            new AssetTrackHead(key, start, end);

        private static StandaloneAssetTrack Standalone(int id, string? key = "fx_impact") =>
            new StandaloneAssetTrack(
                id, Head(key), new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(1f, 1f, 1f), new Vec4(0f, 1f, 0f, 0.5f), new Vec3(0f, 0f, 0f));

        private static ProjectileAssetTrack Projectile(int id, int keys, string? key = "fx_bullet")
        {
            var frames = new TransformKey[keys];
            for (var i = 0; i < keys; i++)
            {
                frames[i] = new TransformKey(
                    i * 0.05f, new Vec3(i, 0f, 0f), new Vec4(0f, 0f, 0f, 1f));
            }
            return new ProjectileAssetTrack(id, Head(key), new Vec3(1f, 1f, 1f), frames);
        }

        private static BeamAssetTrack Beam(int id, int keys, string? key = "fx_beam")
        {
            var frames = new BeamKey[keys];
            for (var i = 0; i < keys; i++)
            {
                frames[i] = new BeamKey(
                    i * 0.05f, new Vec3(i, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0.5f, 0.25f, i));
            }
            return new BeamAssetTrack(id, Head(key), frames);
        }

        private static AssetCapture Capture(
            IReadOnlyList<StandaloneAssetTrack>? standalone = null,
            IReadOnlyList<ProjectileAssetTrack>? projectiles = null,
            IReadOnlyList<BeamAssetTrack>? beams = null) =>
            new AssetCapture(standalone, projectiles, beams);

        // --- preparing one track ---

        [Fact]
        public void TryPrepare_AStandaloneTrack_PassesItThrough()
        {
            var track = Standalone(1);

            Assert.Equal(AssetTrackFault.None, ReplayAssetParts.TryPrepare(track, out var prepared));
            Assert.Same(track, prepared);
        }

        [Fact]
        public void TryPrepare_AProjectileWithEnoughKeys_PassesItThrough()
        {
            var track = Projectile(1, 4);

            Assert.Equal(AssetTrackFault.None, ReplayAssetParts.TryPrepare(track, out var prepared));
            Assert.Equal(4, prepared!.Keys.Count);
            Assert.Equal(track.Id, prepared.Id);
            Assert.Equal(track.Head.AssetKey, prepared.Head.AssetKey);
            Assert.Equal(track.Scale.X, prepared.Scale.X);
        }

        [Fact]
        public void TryPrepare_ABeamWithEnoughKeys_PassesItThrough()
        {
            var track = Beam(1, 4);

            Assert.Equal(AssetTrackFault.None, ReplayAssetParts.TryPrepare(track, out var prepared));
            Assert.Equal(4, prepared!.Keys.Count);
            Assert.Equal(track.Id, prepared.Id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void TryPrepare_AStandaloneWithNoKey_IsUnsendable(string? key)
        {
            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare(Standalone(1, key), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_AProjectileWithNoKey_IsUnsendable()
        {
            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare(Projectile(1, 4, null), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_ABeamWithNoKey_IsUnsendable()
        {
            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare(Beam(1, 4, null), out var prepared));
            Assert.Null(prepared);
        }

        // A key over the cap is caught here rather than at encode, for the
        // reason PoseTrackFault.NameTooLong exists: PbjWriter throws above its
        // own string limit and PbjRuntime.SendTo encodes outside its try block,
        // so a throw there empties the effect pump behind it.
        [Fact]
        public void TryPrepare_AStandaloneWithAnOverlongKey_IsUnsendable()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength + 1);

            Assert.Equal(
                AssetTrackFault.KeyTooLong,
                ReplayAssetParts.TryPrepare(Standalone(1, key), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_AProjectileWithAnOverlongKey_IsUnsendable()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength + 1);

            Assert.Equal(
                AssetTrackFault.KeyTooLong,
                ReplayAssetParts.TryPrepare(Projectile(1, 4, key), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_ABeamWithAnOverlongKey_IsUnsendable()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength + 1);

            Assert.Equal(
                AssetTrackFault.KeyTooLong,
                ReplayAssetParts.TryPrepare(Beam(1, 4, key), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_AKeyExactlyAtTheCap_IsSendable()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength);

            Assert.Equal(
                AssetTrackFault.None,
                ReplayAssetParts.TryPrepare(Standalone(1, key), out _));
        }

        // The origin-freeze hazard, and the reason it is a drop rather than a
        // repair. ReplayEntityAssetProjectile.ApplyTime returns early below two
        // keys, but AssignAsset has ALREADY placed and shown the instance — at
        // keyframes[0], or at the world origin when there are none. A projectile
        // frozen at the origin is the visible result; nothing renders it right.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void TryPrepare_AProjectileBelowTwoKeys_IsUnsendable(int keys)
        {
            Assert.Equal(
                AssetTrackFault.TooFewKeys,
                ReplayAssetParts.TryPrepare(Projectile(1, keys), out var prepared));
            Assert.Null(prepared);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void TryPrepare_ABeamBelowTwoKeys_IsUnsendable(int keys)
        {
            Assert.Equal(
                AssetTrackFault.TooFewKeys,
                ReplayAssetParts.TryPrepare(Beam(1, keys), out var prepared));
            Assert.Null(prepared);
        }

        [Fact]
        public void TryPrepare_AProjectileOverTheKeyCap_ThinsItAndKeepsTheEndpoints()
        {
            var track = Projectile(1, PbjMessageCodec.MaxAssetKeysPerTrack + 40);

            Assert.Equal(AssetTrackFault.None, ReplayAssetParts.TryPrepare(track, out var prepared));
            Assert.Equal(PbjMessageCodec.MaxAssetKeysPerTrack, prepared!.Keys.Count);
            Assert.Equal(track.Keys[0].Time, prepared.Keys[0].Time);
            Assert.Equal(
                track.Keys[track.Keys.Count - 1].Time,
                prepared.Keys[prepared.Keys.Count - 1].Time);
        }

        [Fact]
        public void TryPrepare_ABeamOverTheKeyCap_ThinsItAndKeepsTheEndpoints()
        {
            var track = Beam(1, PbjMessageCodec.MaxAssetKeysPerTrack + 40);

            Assert.Equal(AssetTrackFault.None, ReplayAssetParts.TryPrepare(track, out var prepared));
            Assert.Equal(PbjMessageCodec.MaxAssetKeysPerTrack, prepared!.Keys.Count);
            Assert.Equal(track.Keys[0].Time, prepared.Keys[0].Time);
            Assert.Equal(
                track.Keys[track.Keys.Count - 1].Time,
                prepared.Keys[prepared.Keys.Count - 1].Time);
        }

        [Fact]
        public void TryPrepare_ANullTrack_IsUnsendableRatherThanThrowing()
        {
            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare((StandaloneAssetTrack?)null, out var standalone));
            Assert.Null(standalone);

            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare((ProjectileAssetTrack?)null, out var projectile));
            Assert.Null(projectile);

            Assert.Equal(
                AssetTrackFault.NoKey,
                ReplayAssetParts.TryPrepare((BeamAssetTrack?)null, out var beam));
            Assert.Null(beam);
        }

        // --- splitting a turn into parts ---

        [Fact]
        public void Split_NothingCaptured_ProducesNoPartsAtAll()
        {
            Assert.Empty(ReplayAssetParts.Split(AssetCapture.None, out var dropped));
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void Split_ANullCapture_ProducesNoPartsAtAll()
        {
            Assert.Empty(ReplayAssetParts.Split(null, out var dropped));
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void Split_AFewOfEachKind_ProducesOnePartHoldingThemAll()
        {
            var parts = ReplayAssetParts.Split(
                Capture(
                    new[] { Standalone(1), Standalone(2) },
                    new[] { Projectile(3, 2) },
                    new[] { Beam(4, 2) }),
                out var dropped);

            Assert.Equal(0, dropped);
            var part = Assert.Single(parts);
            Assert.Equal(2, part.Standalone.Count);
            Assert.Single(part.Projectiles);
            Assert.Single(part.Beams);
        }

        [Fact]
        public void Split_OverThePartSize_FillsPartsToTheCapInOrder()
        {
            var cap = PbjMessageCodec.MaxAssetsPerPart;
            var standalone = new StandaloneAssetTrack[cap + 1];
            for (var i = 0; i < standalone.Length; i++)
            {
                standalone[i] = Standalone(i);
            }

            var parts = ReplayAssetParts.Split(Capture(standalone), out var dropped);

            Assert.Equal(0, dropped);
            Assert.Equal(2, parts.Count);
            Assert.Equal(cap, parts[0].Standalone.Count);
            Assert.Single(parts[1].Standalone);
            Assert.Equal(cap, parts[1].Standalone[0].Id);
        }

        // A part is a slice of the concatenated sequence, not a slice of one
        // kind, so a part can straddle two kinds. Held here because the client's
        // buffer reassembles by concatenation and would silently reorder if
        // either side thought in whole kinds.
        [Fact]
        public void Split_AcrossAPartBoundary_StraddlesTheKinds()
        {
            var cap = PbjMessageCodec.MaxAssetsPerPart;
            var standalone = new StandaloneAssetTrack[cap - 1];
            for (var i = 0; i < standalone.Length; i++)
            {
                standalone[i] = Standalone(i);
            }

            var parts = ReplayAssetParts.Split(
                Capture(standalone, new[] { Projectile(90, 2), Projectile(91, 2) }, new[] { Beam(92, 2) }),
                out _);

            Assert.Equal(2, parts.Count);
            Assert.Equal(cap - 1, parts[0].Standalone.Count);
            Assert.Single(parts[0].Projectiles);
            Assert.Equal(90, parts[0].Projectiles[0].Id);
            Assert.Empty(parts[0].Beams);

            Assert.Empty(parts[1].Standalone);
            Assert.Single(parts[1].Projectiles);
            Assert.Equal(91, parts[1].Projectiles[0].Id);
            Assert.Single(parts[1].Beams);
        }

        // The cap is not decoration: a peer sizes its accumulator from the part
        // count it is told, and an unbounded turn is a frame storm the receiver
        // pays for. Dropping the tail is stated rather than silent — the count
        // goes out in a log line.
        [Fact]
        public void Split_PastTheTurnCapacity_DropsTheTailAndSaysHowMany()
        {
            var capacity = PbjMessageCodec.MaxAssetPartsPerTurn * PbjMessageCodec.MaxAssetsPerPart;
            var standalone = new StandaloneAssetTrack[capacity + 5];
            for (var i = 0; i < standalone.Length; i++)
            {
                standalone[i] = Standalone(i);
            }

            var parts = ReplayAssetParts.Split(Capture(standalone), out var dropped);

            Assert.Equal(5, dropped);
            Assert.Equal(PbjMessageCodec.MaxAssetPartsPerTurn, parts.Count);
            var total = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                total += parts[i].Standalone.Count;
            }
            Assert.Equal(capacity, total);
        }
    }
}
