using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // What the host sends when its own turn finishes.
    //
    // The turn-complete handler and the two selectors that decide how much of the
    // capture is sendable.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        private void HandleLocalTurnComplete(LocalTurnCompleteEvent complete, List<PbjEffect> effects)
        {
            if (State != HostSessionState.Executing)
            {
                return;
            }

            // The ECS has already advanced past the executed turn, so report the
            // number captured at commit time rather than reading it back.
            effects.Add(new LogEffect(NetLog.TurnCompleted(committedTurn, complete.Digest, registry.Count)));
            effects.Add(new BroadcastEffect(new TurnCompleteMessage(committedTurn, complete.Digest)));

            // TurnComplete first, then the correction. Snapshot-first would make
            // the client's digest already match by the time it compared, silencing
            // the divergence diagnostic forever — and the client really is
            // diverged at that moment, having not simulated anything.
            effects.Add(new LogEffect(NetLog.SnapshotSent(committedTurn, complete.Units.Count, registry.Count)));
            effects.Add(new BroadcastEffect(
                new SnapshotMessage(committedTurn, complete.Digest, complete.Units)));

            // Keyframes last. They are presentation, and the correction the
            // digest is checked against must never queue behind them. A turn
            // with nothing recorded — prediction disabled, so the game never
            // started its replay recorder — sends nothing at all rather than an
            // empty message a client would have to special-case.
            var keyframes = complete.Keyframes;
            if (keyframes.Tracks.Count > 0)
            {
                var keyCount = 0;
                for (var i = 0; i < keyframes.Tracks.Count; i++)
                {
                    keyCount += keyframes.Tracks[i].Transforms.Count;
                }
                // M8's poses go out FIRST, inside this same guard, because the
                // Keyframes broadcast below is what tells a client the set is
                // complete. Emitting them outside it would let a turn with no
                // transform tracks send poses that nothing ever terminates —
                // the one orphan shape the client cannot resolve.
                var posed = SendablePoses(committedTurn, keyframes.Poses, effects);
                for (var i = 0; i < posed.Count; i++)
                {
                    effects.Add(new BroadcastEffect(
                        new PosesMessage(committedTurn, i, posed.Count, posed[i])));
                }
                if (posed.Count > 0)
                {
                    effects.Add(new LogEffect(
                        NetLog.PosesSent(committedTurn, posed.Count, registry.Count)));
                }

                // M14's effects follow the poses and share their terminator.
                // Inside this same guard for the same reason: the Keyframes
                // broadcast below is what tells a client the set is complete,
                // and parts emitted outside it would wait on a terminator that
                // never comes.
                var assetParts = SendableAssets(committedTurn, keyframes.Assets, effects);
                var assetTracks = 0;
                for (var i = 0; i < assetParts.Count; i++)
                {
                    effects.Add(new BroadcastEffect(new ReplayAssetsMessage(
                        committedTurn, i, assetParts.Count, assetParts[i])));
                    assetTracks += assetParts[i].Standalone.Count
                        + assetParts[i].Projectiles.Count
                        + assetParts[i].Beams.Count;
                }
                if (assetParts.Count > 0)
                {
                    effects.Add(new LogEffect(NetLog.AssetsSent(
                        committedTurn, assetParts.Count, assetTracks, registry.Count)));
                }

                effects.Add(new LogEffect(NetLog.KeyframesSent(
                    committedTurn, keyframes.Tracks.Count, keyCount,
                    keyframes.WindowStart, keyframes.WindowEnd, registry.Count)));
                effects.Add(new BroadcastEffect(new KeyframesMessage(
                    committedTurn, keyframes.WindowStart, keyframes.WindowEnd, keyframes.Tracks)));
            }
            else if (!keyframes.Assets.IsEmpty)
            {
                // The one shape the guard costs something real, and it must not
                // be silent. M8 priced this shape for poses and the cost was
                // genuinely zero — a turn with no transform tracks has no units
                // to pose. It is NOT zero for effects: capture drops destroyed
                // units, so a mutual-destruction final volley can record a turn
                // full of explosions and no surviving unit to carry a track.
                // That is the fight's climax, and without this line it goes
                // missing with nothing said on either machine — the client
                // never even gets a terminator to report against.
                effects.Add(new LogEffect(NetLog.AssetsWithoutTracks(
                    committedTurn,
                    keyframes.Assets.Standalone.Count
                        + keyframes.Assets.Projectiles.Count
                        + keyframes.Assets.Beams.Count)));
            }

            barrier.AdvanceTo(bridge.CurrentTurn);
            State = HostSessionState.Planning;
            effects.Add(new SetExecutionLockEffect(false));
        }

        /// <summary>
        /// The pose tracks that may travel, or none at all.
        /// </summary>
        /// <remarks>
        /// All or nothing, per turn, and that is the whole point of the method.
        /// Repairable faults are repaired by <see cref="PoseTracks.TryPrepare"/>;
        /// a track it cannot repair takes the entire turn down to transform-only
        /// rather than being quietly omitted. Omitting it would leave one unit
        /// sliding among walking ones, which reads as a broken game, whereas
        /// every unit sliding reads as the lower-fidelity mode it actually is.
        /// <para>
        /// The exception is a track with too few keys to animate. That one is
        /// dropped alone and deliberately, because the host's own replay does
        /// not animate it either — the game gates its pose block on more than
        /// two keys — so skipping it shows the client exactly what the host sees.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<UnitPoseTrack> SendablePoses(
            int turn, IReadOnlyList<UnitPoseTrack> captured, List<PbjEffect> effects)
        {
            var sendable = new List<UnitPoseTrack>(captured.Count);
            for (var i = 0; i < captured.Count; i++)
            {
                var fault = PoseTracks.TryPrepare(captured[i], out var prepared);
                if (fault == PoseTrackFault.None)
                {
                    sendable.Add(prepared!);
                    continue;
                }
                if (fault == PoseTrackFault.TooFewKeys)
                {
                    continue;
                }

                effects.Add(new LogEffect(
                    NetLog.PosesUnsendable(turn, fault, captured[i].Name)));
                return NoPoses;
            }

            return sendable;
        }

        /// <summary>
        /// The turn's effects, checked over and cut into parts.
        /// </summary>
        /// <remarks>
        /// Per-track dropping, which is the deliberate opposite of
        /// <see cref="SendablePoses"/>'s all-or-nothing. One unit sliding among
        /// walking ones is a mixture that reads as a broken game; one impact
        /// flash missing from a turn's worth of impact flashes is invisible,
        /// and is a shape the host's own pool exhaustion can produce anyway.
        /// Demoting every effect for one bad key would trade an invisible loss
        /// for a visible one.
        /// </remarks>
        private static IReadOnlyList<AssetCapture> SendableAssets(
            int turn, AssetCapture captured, List<PbjEffect> effects)
        {
            var standalone = new List<StandaloneAssetTrack>(captured.Standalone.Count);
            var projectiles = new List<ProjectileAssetTrack>(captured.Projectiles.Count);
            var beams = new List<BeamAssetTrack>(captured.Beams.Count);
            var dropped = 0;
            var reason = AssetTrackFault.None;

            for (var i = 0; i < captured.Standalone.Count; i++)
            {
                var fault = ReplayAssetParts.TryPrepare(captured.Standalone[i], out var prepared);
                if (fault == AssetTrackFault.None)
                {
                    standalone.Add(prepared!);
                    continue;
                }
                dropped++;
                reason = fault;
            }

            for (var i = 0; i < captured.Projectiles.Count; i++)
            {
                var fault = ReplayAssetParts.TryPrepare(captured.Projectiles[i], out var prepared);
                if (fault == AssetTrackFault.None)
                {
                    projectiles.Add(prepared!);
                    continue;
                }
                dropped++;
                reason = fault;
            }

            for (var i = 0; i < captured.Beams.Count; i++)
            {
                var fault = ReplayAssetParts.TryPrepare(captured.Beams[i], out var prepared);
                if (fault == AssetTrackFault.None)
                {
                    beams.Add(prepared!);
                    continue;
                }
                dropped++;
                reason = fault;
            }

            if (dropped > 0)
            {
                effects.Add(new LogEffect(NetLog.AssetsDropped(turn, dropped, reason)));
            }

            var parts = ReplayAssetParts.Split(
                new AssetCapture(standalone, projectiles, beams), out var overCapacity);
            if (overCapacity > 0)
            {
                effects.Add(new LogEffect(NetLog.AssetsOverCapacity(turn, overCapacity)));
            }
            return parts;
        }
    }
}
