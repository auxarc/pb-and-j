using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Content.Code.Utility;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PBAndJ.Core.Net;
using PBAndJ.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // pbj.replay-last -- the M6 gate. Round-trips the last executed turn's
    // captured motion through the codec before playing it, so one command
    // exercises the whole pipeline a client depends on.
    internal static partial class NetGlue
    {
        /// <summary>
        /// Keeps what a client was just told to play, so it can play it again.
        /// </summary>
        /// <remarks>
        /// Without this <c>pbj.replay-last</c> is host-only, because
        /// <c>lastCapture</c> is otherwise written solely by the host's own
        /// capture path — and the client is the machine whose playback anyone
        /// actually wants to inspect twice. Re-running a turn to look at it
        /// again means re-authoring orders and re-executing on the host, which
        /// makes any A/B comparison a comparison of two different turns.
        /// <para>
        /// The stored capture has already crossed the wire, so replaying it
        /// sends it through the codec a second time. That is deliberate and
        /// costs nothing: one code path, and a capture the encoder would now
        /// refuse is worth learning about here.
        /// </para>
        /// </remarks>
        internal static void RememberPlayed(int turn, KeyframeCapture capture)
        {
            lastCapture = capture;
            lastCaptureTurn = turn;
        }

        /// <summary>
        /// Replays the last executed turn's captured motion on this machine.
        /// </summary>
        /// <remarks>
        /// The M6 gate. Deliberately round-trips the tracks through the codec
        /// before playing them, so one command exercises the whole pipeline a
        /// client depends on — capture, re-key, turn slicing, encode, decode,
        /// sample, render — with a single game instance. Playing the in-memory
        /// capture directly would prove only that capture works.
        /// <para>
        /// Safe on a host because it writes view transforms only. Authoritative
        /// ECS state is untouched, and the next execution's TransformLinkSystem
        /// pass restores every view regardless. Expect units to slide rather than
        /// walk: poses are out of scope, and sliding is exactly what a client
        /// sees today.
        /// </para>
        /// </remarks>
        public static string ReplayLast()
        {
            if (lastCapture == null || lastCapture.Tracks.Count == 0)
            {
                return "[pb-and-j] no keyframes captured yet — execute a turn first";
            }

            KeyframesMessage decoded;
            try
            {
                var wire = PbjMessageCodec.Encode(new KeyframesMessage(
                    lastCaptureTurn, lastCapture.WindowStart, lastCapture.WindowEnd, lastCapture.Tracks));
                decoded = (KeyframesMessage)PbjMessageCodec.Decode(wire);
            }
            catch (PbjProtocolException e)
            {
                // A capture the codec refuses would have been dropped silently on
                // the wire. Better to learn it here.
                return "[pb-and-j] captured keyframes failed the codec round-trip: " + e.Message;
            }

            var keys = 0;
            foreach (var track in decoded.Tracks)
            {
                keys += track.Transforms.Count;
            }

            // The poses go through the codec too, and for two reasons. The
            // round trip is the same cheap proof the transforms get — a track
            // the encoder would refuse is better learned here than by having
            // the receiving peer drop us as malformed. And it makes this
            // command a genuine one-instance eyeball test of M8: execute a
            // turn, run it, and watch whether the mechs walk. Without it the
            // only way to see a pose is to stand up two games.
            var poses = new List<UnitPoseTrack>();
            try
            {
                foreach (var pose in lastCapture.Poses)
                {
                    if (PoseTracks.TryPrepare(pose, out var prepared) != PoseTrackFault.None)
                    {
                        continue;
                    }
                    var wire = PbjMessageCodec.Encode(new PosesMessage(lastCaptureTurn, 0, 1, prepared));
                    poses.Add(((PosesMessage)PbjMessageCodec.Decode(wire)).Track!);
                }
            }
            catch (PbjProtocolException e)
            {
                return "[pb-and-j] captured poses failed the codec round-trip: " + e.Message;
            }

            // M14's effects take the same trip, and through the SPLIT and the
            // client's own accumulator rather than a single message — that is
            // the half a one-instance test can still prove, and the half that
            // fails invisibly: a part boundary off by one is a turn that
            // reassembles into nothing, which looks exactly like a client where
            // the feature was never built.
            var assets = AssetCapture.None;
            try
            {
                var parts = ReplayAssetParts.Split(Sendable(lastCapture.Assets), out _);
                if (parts.Count > 0)
                {
                    var buffer = new AssetBuffer();
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var wire = PbjMessageCodec.Encode(
                            new ReplayAssetsMessage(lastCaptureTurn, i, parts.Count, parts[i]));
                        buffer.Accept((ReplayAssetsMessage)PbjMessageCodec.Decode(wire));
                    }
                    assets = buffer.Take(lastCaptureTurn);
                }
            }
            catch (PbjProtocolException e)
            {
                return "[pb-and-j] captured effects failed the codec round-trip: " + e.Message;
            }

            KeyframePlayer.Play(decoded.Turn, new KeyframeCapture(
                decoded.WindowStart, decoded.WindowEnd, decoded.Tracks, poses, assets));
            if (!KeyframePlayer.IsPlaying)
            {
                return "[pb-and-j] replay: no recorded unit is present in this combat";
            }

            var line = NetLog.KeyframesReceived(
                decoded.Turn, decoded.Tracks.Count, keys, decoded.WindowStart, decoded.WindowEnd);
            Debug.Log(line);
            Debug.Log(KeyframePlayer.PosedUnits > 0
                ? NetLog.PosesReceived(decoded.Turn, KeyframePlayer.PosedUnits)
                : NetLog.PosesIncomplete(decoded.Turn, poses.Count, lastCapture.Poses.Count));

            var effects = assets.Standalone.Count + assets.Projectiles.Count + assets.Beams.Count;
            Debug.Log(effects > 0
                ? NetLog.AssetsReceived(decoded.Turn, effects)
                : NetLog.AssetsNoneSent(decoded.Turn));
            return line;
        }

        /// <summary>
        /// The captured effects that could travel, checked the way the host
        /// would check them.
        /// </summary>
        /// <remarks>
        /// Applied here so <c>pbj.replay-last</c> shows what a client would
        /// actually receive rather than what the recorder happened to hold. The
        /// per-track drop is the point: a projectile stranded below two keys is
        /// dropped by the host too, and a replay that showed it anyway would be
        /// a more forgiving test than the wire.
        /// </remarks>
        private static AssetCapture Sendable(AssetCapture captured)
        {
            var standalone = new List<StandaloneAssetTrack>(captured.Standalone.Count);
            for (var i = 0; i < captured.Standalone.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Standalone[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    standalone.Add(prepared!);
                }
            }

            var projectiles = new List<ProjectileAssetTrack>(captured.Projectiles.Count);
            for (var i = 0; i < captured.Projectiles.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Projectiles[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    projectiles.Add(prepared!);
                }
            }

            var beams = new List<BeamAssetTrack>(captured.Beams.Count);
            for (var i = 0; i < captured.Beams.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Beams[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    beams.Add(prepared!);
                }
            }

            return new AssetCapture(standalone, projectiles, beams);
        }
    }
}
