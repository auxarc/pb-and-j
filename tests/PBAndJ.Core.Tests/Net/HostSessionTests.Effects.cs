using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Replayed effects (M14): beams, trails, projectiles and the per-turn caps on them.
    // One section of the original.
    //
    // Burst, Shot, Lance, MotionWithAssets and AssetsOf are used only here.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- replayed effects (M14) ---

        private static StandaloneAssetTrack Burst(int id, string? key = "fx_impact") =>
            new StandaloneAssetTrack(
                id, new AssetTrackHead(key, 15f, 16f), new Vec3(1f, 0f, 1f),
                new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f), default, default);

        private static ProjectileAssetTrack Shot(int id, int keys = 3) =>
            new ProjectileAssetTrack(
                id, new AssetTrackHead("fx_bullet", 15f, 16f), new Vec3(1f, 1f, 1f),
                Enumerable.Range(0, keys)
                    .Select(i => new TransformKey(15f + (i * 0.1f), new Vec3(i, 0f, 0f), default))
                    .ToArray());

        private static BeamAssetTrack Lance(int id, int keys = 3) =>
            new BeamAssetTrack(
                id, new AssetTrackHead("fx_beam", 15f, 16f),
                Enumerable.Range(0, keys)
                    .Select(i => new BeamKey(15f + (i * 0.1f), new Vec3(i, 0f, 0f), default, default))
                    .ToArray());

        private static KeyframeCapture MotionWithAssets(
            IReadOnlyList<StandaloneAssetTrack>? standalone = null,
            IReadOnlyList<ProjectileAssetTrack>? projectiles = null,
            IReadOnlyList<BeamAssetTrack>? beams = null) =>
            new KeyframeCapture(
                15f, 20f, Motion().Tracks, null,
                new AssetCapture(standalone, projectiles, beams));

        private static IReadOnlyList<ReplayAssetsMessage> AssetsOf(IEnumerable<PbjEffect> effects) =>
            All<BroadcastEffect>(effects).Select(b => b.Message)
                .OfType<ReplayAssetsMessage>().ToList();

        // Same ordering rule the poses follow, and for the same reason: the
        // transform keyframes are the terminator, so anything they terminate
        // has to precede them.
        [Fact]
        public void TurnComplete_BroadcastsEffectsBeforeTheKeyframesThatTerminateThem()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent(
                    "abc", new[] { Snap("unit_a") },
                    MotionWithAssets(new[] { Burst(1) }, new[] { Shot(2) }, new[] { Lance(3) })))
                .ToList();

            var assetsAt = effects.FindIndex(
                e => e is BroadcastEffect b && b.Message is ReplayAssetsMessage);
            var keyframesAt = effects.FindIndex(
                e => e is BroadcastEffect b && b.Message is KeyframesMessage);
            Assert.True(assetsAt >= 0 && keyframesAt > assetsAt);
        }

        [Fact]
        public void TurnComplete_CarriesEveryKindInOnePartWhenTheyFit()
        {
            var parts = AssetsOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWithAssets(new[] { Burst(1) }, new[] { Shot(2) }, new[] { Lance(3) }))));

            var part = Assert.Single(parts);
            Assert.Equal(0, part.PartIndex);
            Assert.Equal(1, part.PartCount);
            Assert.Equal(3, part.Turn);
            Assert.Single(part.Assets.Standalone);
            Assert.Single(part.Assets.Projectiles);
            Assert.Single(part.Assets.Beams);
        }

        [Fact]
        public void TurnComplete_WithNoEffectsRecorded_BroadcastsNoParts()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()));

            Assert.Empty(AssetsOf(effects));
        }

        [Fact]
        public void TurnComplete_EffectsAreReportedWithTheirCounts()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWithAssets(new[] { Burst(1), Burst(2) }, new[] { Shot(3) }, null)));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("3 tracks in 1 part"));
        }

        // Per-track dropping, the deliberate opposite of the pose rule in
        // HostSessionTests.Motion.cs, which demotes the whole turn.
        // One impact missing from a turn's worth of impacts is invisible — and
        // is a shape the host's own pool exhaustion produces anyway — whereas
        // demoting every effect for one bad key trades an invisible loss for a
        // visible one.
        [Fact]
        public void TurnComplete_AnUnsendableEffect_DropsThatOneAndKeepsTheRest()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWithAssets(
                    new[] { Burst(1), Burst(2, null) },
                    new[] { Shot(3), Shot(4, keys: 1) },
                    new[] { Lance(5), Lance(6, keys: 0) }))).ToList();

            var part = Assert.Single(AssetsOf(effects));
            Assert.Equal(1, Assert.Single(part.Assets.Standalone).Id);
            Assert.Equal(3, Assert.Single(part.Assets.Projectiles).Id);
            Assert.Equal(5, Assert.Single(part.Assets.Beams).Id);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("3 tracks dropped"));
        }

        [Fact]
        public void TurnComplete_ThinsAnOversampledProjectileRatherThanRefusingIt()
        {
            var parts = AssetsOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                MotionWithAssets(
                    null, new[] { Shot(1, PbjMessageCodec.MaxAssetKeysPerTrack + 20) }, null))));

            Assert.Equal(
                PbjMessageCodec.MaxAssetKeysPerTrack,
                Assert.Single(parts).Assets.Projectiles[0].Keys.Count);
        }

        [Fact]
        public void TurnComplete_MoreEffectsThanOnePartHolds_SplitsThemAndNumbersTheParts()
        {
            var bursts = Enumerable.Range(0, PbjMessageCodec.MaxAssetsPerPart + 1)
                .Select(i => Burst(i)).ToArray();

            var parts = AssetsOf(Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") }, MotionWithAssets(bursts))));

            Assert.Equal(2, parts.Count);
            Assert.Equal(new[] { 0, 1 }, parts.Select(p => p.PartIndex));
            Assert.All(parts, p => Assert.Equal(2, p.PartCount));
        }

        // A backstop rather than a working limit — capacity is four thousand-odd
        // tracks against a measured worst case of 1091 — but it must never be
        // silent. A fight that reaches it is telling us the caps were measured
        // against the wrong fight.
        [Fact]
        public void TurnComplete_PastThePerTurnCapacity_DropsTheTailAndSaysSo()
        {
            var capacity = PbjMessageCodec.MaxAssetPartsPerTurn * PbjMessageCodec.MaxAssetsPerPart;
            var bursts = Enumerable.Range(0, capacity + 3).Select(i => Burst(i)).ToArray();

            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") }, MotionWithAssets(bursts))).ToList();

            Assert.Equal(PbjMessageCodec.MaxAssetPartsPerTurn, AssetsOf(effects).Count);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("past the per-turn cap"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("3 tracks dropped"));
        }

        // Effects live inside the same guard as the terminator, exactly as the
        // poses do: parts nothing ever terminates are the one shape the client
        // cannot resolve.
        [Fact]
        public void TurnComplete_WithEffectsButNoTransformTracks_SendsNothingAtAll()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                new KeyframeCapture(15f, 20f, null, null, new AssetCapture(
                    new[] { Burst(1) }, null, null))));

            Assert.Empty(AssetsOf(effects));
            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
        }

        // ...but unlike the poses, that guard costs something real here, so it
        // must not be silent. Capture drops destroyed units, so a mutual
        // destruction final volley records a turn full of explosions with no
        // surviving unit to carry a track — and the client cannot report the
        // loss either, having never received the terminator it would report
        // against. This line is the only place it can be said.
        [Fact]
        public void TurnComplete_WithEffectsButNoTransformTracks_SaysWhatWasDiscarded()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") },
                new KeyframeCapture(15f, 20f, null, null, new AssetCapture(
                    new[] { Burst(1), Burst(2) }, new[] { Shot(3) }, null))));

            Assert.Contains(
                All<LogEffect>(effects), l => l.Line.Contains("3 effect tracks but no unit motion"));
        }

        // And a genuinely empty turn stays quiet, or the line above would fire
        // on every turn a scenario runs with the recorder off.
        [Fact]
        public void TurnComplete_WithNeitherEffectsNorTracks_SaysNothingAboutEffects()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent(
                "abc", new[] { Snap("unit_a") }, new KeyframeCapture(15f, 20f, null)));

            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("effect track"));
        }

        [Fact]
        public void ReplayAssets_FromAPeer_AreAProtocolViolationTheSameWay()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReplayAssetsMessage(3, 0, 1, null));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        // Keyframes are client-bound, so a peer sending them upward is a protocol
        // violation and gets the same treatment Snapshot or Welcome would. Pinned
        // because the temptation with a new message type is to give it a quiet
        // ignore-arm, which would make it the one client-bound message a peer may
        // forge freely.
        // These two are about M6 keyframe and M8 pose messages, not M14 effects --
        // the ReplayAssets test above is the M14 member of the same trio. All three
        // were filed under this section in the original and moved with it.
        [Fact]
        public void Keyframes_FromAPeer_AreAProtocolViolationLikeAnyClientBoundMessage()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new KeyframesMessage(3, 0f, 5f, null));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void Poses_FromAPeer_AreAProtocolViolationTheSameWay()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new PosesMessage(3, 0, 1, null));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }
    }
}
