using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Threading;
using PBAndJ.Core.Net;
using PBAndJ.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// The asset-fallback scenario.
    /// </summary>
    /// <remarks>
    /// One part of <c>SelfTest</c>, which is a single class split across
    /// files. The scenario table in SelfTest.cs is checked against the
    /// methods declared here at run time, so a part whose registration is
    /// lost fails loudly rather than silently running fewer scenarios.
    /// </remarks>
    internal static partial class SelfTest
    {
        /// <summary>
        /// A turn of effects too large for one part, and the ways one fails.
        /// </summary>
        /// <remarks>
        /// The split is the half of M14 that no eyeball can check. A part
        /// boundary off by one is not a wrong-looking effect, it is a turn the
        /// client can never reassemble — so nothing fires, which looks exactly
        /// like the feature not being built. Nothing outside this scenario
        /// drives more than one part.
        /// <para>
        /// The dropping arm is the deliberate opposite of
        /// <see cref="RunPoseFallbacks"/>'s: effects drop <b>per track</b> and
        /// the rest of the turn plays. One impact missing from a turn's worth of
        /// impacts is invisible — and is a shape the game's own pool exhaustion
        /// produces anyway — where one unit sliding among walking ones reads as
        /// a broken game.
        /// </para>
        /// </remarks>
        private static int RunAssetFallbacks()
        {
            var hostBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var hostLog = new PrefixedLog("host");
            var host = new PbjRuntime(hostTransport, hostBridge, hostLog, hostMailbox, hostSession);

            var clientBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var clientMailbox = new PbjMailbox(4096);
            var clientTransport = new TcpClientTransport(clientMailbox);
            var clientSession = new ClientSession("ally", "0.2.0", clientBridge);
            var client = new PbjRuntime(
                clientTransport, clientBridge, new PrefixedLog("ally"), clientMailbox, clientSession);

            var clock = Stopwatch.StartNew();
            double Now() => clock.Elapsed.TotalSeconds;

            bool WaitFor(string what, Func<bool> condition)
            {
                var deadline = Now() + TimeoutSeconds;
                while (Now() < deadline)
                {
                    host.Pump(Now());
                    client.Pump(Now());
                    if (condition())
                    {
                        Console.WriteLine($"[selftest] OK   {what}");
                        return true;
                    }
                    Thread.Sleep(5);
                }
                Console.WriteLine($"[selftest] FAIL {what}");
                return false;
            }

            const float windowStart = 0f;
            const float windowEnd = 5f;

            // Effects ride inside the host's Tracks.Count > 0 guard, so a turn
            // with no transform tracks sends none of them and would prove
            // nothing about any of the arms below.
            List<UnitTrack> Transforms()
            {
                var tracks = new List<UnitTrack>();
                foreach (var unit in hostBridge.Units)
                {
                    tracks.Add(new UnitTrack(unit.Name, new[]
                    {
                        new TransformKey(windowStart, unit.Position, unit.Rotation),
                        new TransformKey(windowEnd, unit.Position, unit.Rotation),
                    }));
                }
                return tracks;
            }

            bool DriveTurn(string what, int label, AssetCapture assets)
            {
                client.Post(new LocalReadyEvent());
                host.Post(new LocalReadyEvent());
                if (!WaitFor($"{what}: turn {label} began executing",
                        () => hostSession.State == HostSessionState.Executing))
                {
                    return false;
                }

                hostBridge.Keyframes = new KeyframeCapture(
                    windowStart, windowEnd, Transforms(), null, assets);
                host.Post(new LocalTurnCompleteEvent(
                    hostBridge.ComputeStateDigest(),
                    hostBridge.CaptureSnapshot(),
                    hostBridge.CaptureKeyframes()));

                return WaitFor($"{what}: turn {label} reached the client",
                    () => clientBridge.PlayedTurn == label);
            }

            try
            {
                clientTransport.Connect("127.0.0.1", hostTransport.Port);
                if (!WaitFor("handshake completed",
                        () => clientSession.State == ClientSessionState.Planning && hostSession.Peers.Count == 1))
                {
                    return 1;
                }

                // --- turn 3: more effects than one part holds. The measured
                // fight carried 727 standalone effects in a turn, so several
                // parts is the ordinary case rather than the exotic one — and a
                // part carries a slice of the three collections CONCATENATED, so
                // this also drives a part that straddles two kinds.
                var perPart = PbjMessageCodec.MaxAssetsPerPart;
                var many = BuildAssetCapture(
                    seed: 2, windowStart, windowEnd,
                    standaloneCount: perPart - 1, projectileCount: 3, beamCount: 2);
                if (!DriveTurn("multi-part", 3, many))
                {
                    return 1;
                }

                var played = clientBridge.Played!;
                if (!SameAssets(many, played.Assets, out var why))
                {
                    Console.WriteLine($"[selftest] FAIL a split turn did not reassemble: {why}");
                    return 1;
                }
                Console.WriteLine(
                    $"[selftest] OK   {perPart + 4} effects crossed in parts and reassembled in order");

                // --- turn 4: an unsendable track goes alone. Three faults at
                // once, one of each kind, so the per-kind checks cannot pass by
                // sharing one code path.
                var mixed = BuildAssetCapture(
                    seed: 3, windowStart, windowEnd,
                    standaloneCount: 2, projectileCount: 2, beamCount: 2);
                var faulted = new AssetCapture(
                    new[]
                    {
                        mixed.Standalone[0],

                        // No pool key: nothing on the client could resolve it.
                        new StandaloneAssetTrack(
                            99, new AssetTrackHead(null, windowStart, windowEnd),
                            default, default, new Vec3(1f, 1f, 1f), default, default),
                    },
                    new[]
                    {
                        mixed.Projectiles[0],

                        // One key. AssignAsset would already have placed and
                        // shown the instance before ApplyTime's early return —
                        // at keyframes[0], or at the world origin with none.
                        new ProjectileAssetTrack(
                            98, new AssetTrackHead("fx_bullet_short", windowStart, windowEnd),
                            new Vec3(1f, 1f, 1f),
                            new[] { new TransformKey(windowStart, default, default) }),
                    },
                    new[]
                    {
                        mixed.Beams[0],
                        new BeamAssetTrack(
                            97, new AssetTrackHead("fx_beam_empty", windowStart, windowEnd), null),
                    });
                if (!DriveTurn("one bad track of each kind", 4, faulted))
                {
                    return 1;
                }

                played = clientBridge.Played!;
                var kept = new AssetCapture(
                    new[] { mixed.Standalone[0] },
                    new[] { mixed.Projectiles[0] },
                    new[] { mixed.Beams[0] });
                if (!SameAssets(kept, played.Assets, out why))
                {
                    Console.WriteLine($"[selftest] FAIL the good tracks did not survive the bad ones: {why}");
                    return 1;
                }
                if (played.Tracks.Count != 3)
                {
                    Console.WriteLine("[selftest] FAIL dropping effects disturbed the transform tracks");
                    return 1;
                }
                if (!hostLog.Saw("turn 4 effects: 3 tracks dropped"))
                {
                    Console.WriteLine("[selftest] FAIL the dropped effects were never explained in the log");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   three bad tracks dropped alone, the rest of the turn played");

                // --- turn 5: an oversampled projectile is thinned, not dropped.
                // These come off the same player-configurable sampler the poses
                // do, so this is a slider away rather than a hypothetical.
                var oversampled = BuildAssetCapture(
                    seed: 4, windowStart, windowEnd,
                    standaloneCount: 0, projectileCount: 1, beamCount: 1,
                    keyCount: PbjMessageCodec.MaxAssetKeysPerTrack + 40);
                if (!DriveTurn("oversampled", 5, oversampled))
                {
                    return 1;
                }

                played = clientBridge.Played!;
                if (played.Assets.Projectiles.Count != 1 || played.Assets.Beams.Count != 1)
                {
                    Console.WriteLine("[selftest] FAIL thinning dropped a track instead of repairing it");
                    return 1;
                }

                var thinnedShot = played.Assets.Projectiles[0];
                var thinnedBeam = played.Assets.Beams[0];
                if (thinnedShot.Keys.Count != PbjMessageCodec.MaxAssetKeysPerTrack
                    || thinnedBeam.Keys.Count != PbjMessageCodec.MaxAssetKeysPerTrack)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL thinned to {thinnedShot.Keys.Count}/{thinnedBeam.Keys.Count} keys, "
                        + $"not {PbjMessageCodec.MaxAssetKeysPerTrack}");
                    return 1;
                }

                var sentShot = oversampled.Projectiles[0];
                if (thinnedShot.Keys[0].Time != sentShot.Keys[0].Time
                    || thinnedShot.Keys[thinnedShot.Keys.Count - 1].Time
                        != sentShot.Keys[sentShot.Keys.Count - 1].Time)
                {
                    Console.WriteLine("[selftest] FAIL thinning did not keep both endpoints");
                    return 1;
                }
                Console.WriteLine(
                    $"[selftest] OK   {sentShot.Keys.Count} keys thinned to {thinnedShot.Keys.Count}, "
                    + "both endpoints intact");

                // Three turns of effect faults and the session is still whole.
                // This is the assertion the multi-part turn exists for: an
                // over-long frame is not a wrong-looking effect, it is a frame
                // the receiver rejects as malformed, which drops the host.
                if (clientSession.State != ClientSessionState.Planning || hostSession.Peers.Count != 1)
                {
                    Console.WriteLine("[selftest] FAIL the session did not survive the effect turns");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the session survived every effect fault");

                Console.WriteLine("[selftest] PASS");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[selftest] FAIL {e.GetType().Name}: {e.Message}");
                return 1;
            }
            finally
            {
                client.Stop();
                host.Stop();
            }
        }
    }
}
