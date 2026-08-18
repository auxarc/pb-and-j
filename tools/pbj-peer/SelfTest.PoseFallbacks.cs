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
    // The pose-fallback scenario.
    //
    // One part of SelfTest, a single class split across files. Class-level
    // XML doc lives ONLY in SelfTest.cs: /// on a partial part is concatenated
    // by the compiler into one type entry, so eleven parts would produce
    // eleven summaries glued together. Caught by diffing the emitted XML.
    internal static partial class SelfTest
    {
        /// <summary>
        /// The three ways a turn's poses fail, and what each one costs.
        /// </summary>
        /// <remarks>
        /// Its own scenario rather than more of <see cref="RunKeyframeStream"/>
        /// because each arm needs a whole executed turn of its own, and because
        /// a failure here means something different: the happy path proves poses
        /// arrive, this proves the host decides correctly when they cannot.
        /// <para>
        /// Every arm is invisible from inside the game. An over-cap track does
        /// not look wrong, it makes the receiver reject the frame and drop the
        /// host — silently, every turn. A dropped track and a demoted turn both
        /// just look like units sliding. So the ordering is deliberate: the
        /// over-cap turn goes first and two more turns follow it, which is what
        /// proves the peer survived it rather than merely that one message
        /// decoded.
        /// </para>
        /// <para>
        /// One shape is deliberately absent. An <i>incomplete</i> set — parts
        /// sent that never arrive — cannot be staged here, because TCP does not
        /// lose them and there is one send site. It is reachable only from a
        /// host that stopped mid-burst, and the client's response to it (fall
        /// back, log the count) is unit-tested on <c>PoseBuffer</c> directly.
        /// </para>
        /// </remarks>
        private static int RunPoseFallbacks()
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

            // Transform tracks are the constant across all three turns, and they
            // have to be present: poses ride inside the host's
            // Tracks.Count > 0 guard, so a turn with no transforms sends no
            // poses at all and would prove nothing about the fault paths.
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

            bool DriveTurn(string what, int label, IReadOnlyList<UnitPoseTrack> poses)
            {
                client.Post(new LocalReadyEvent());
                host.Post(new LocalReadyEvent());
                if (!WaitFor($"{what}: turn {label} began executing",
                        () => hostSession.State == HostSessionState.Executing))
                {
                    return false;
                }

                hostBridge.Keyframes = new KeyframeCapture(windowStart, windowEnd, Transforms(), poses);
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

                // --- turn 3: a track past the key cap is thinned, not dropped.
                // The sampling interval is a player-facing setting with a
                // 0.016 s floor, so a five-second turn really does record past
                // three hundred keys on a host that only moved a slider.
                var overCap = new List<UnitPoseTrack>
                {
                    BuildPoseTrack(hostBridge.Units[0].Name, 1, windowStart, windowEnd, 300, 3),
                    BuildPoseTrack(hostBridge.Units[1].Name, 2, windowStart, windowEnd, 4, 3),
                    BuildPoseTrack(hostBridge.Units[2].Name, 3, windowStart, windowEnd, 4, 3),
                };
                if (!DriveTurn("over-cap", 3, overCap))
                {
                    return 1;
                }

                var played = clientBridge.Played!;
                if (played.Poses.Count != 3)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL thinning lost tracks: {played.Poses.Count} of 3 arrived");
                    return 1;
                }

                var thinned = FindPose(played.Poses, hostBridge.Units[0].Name);
                var original = overCap[0];
                if (thinned == null || thinned.Keys.Count != PbjMessageCodec.MaxPoseKeysPerTrack)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL the over-cap track arrived with {thinned?.Keys.Count} keys, "
                        + $"not {PbjMessageCodec.MaxPoseKeysPerTrack}");
                    return 1;
                }

                var lastSent = original.Keys[original.Keys.Count - 1];
                var lastGot = thinned.Keys[thinned.Keys.Count - 1];
                if (thinned.Keys[0].Time != original.Keys[0].Time
                    || lastGot.Time != lastSent.Time
                    || lastGot.Joints[0].Position.X != lastSent.Joints[0].Position.X)
                {
                    Console.WriteLine("[selftest] FAIL thinning did not keep both endpoints");
                    return 1;
                }
                Console.WriteLine(
                    $"[selftest] OK   300 keys thinned to {thinned.Keys.Count}, both endpoints intact");

                // --- turn 4: a track too short to animate is dropped alone.
                // The host's own replay gates its pose block on more than two
                // keys, so skipping it shows the client exactly what the host
                // shows — which is why this one fault is per-track.
                var oneShort = new List<UnitPoseTrack>
                {
                    BuildPoseTrack(hostBridge.Units[0].Name, 1, windowStart, windowEnd, 4, 3),
                    BuildPoseTrack(hostBridge.Units[1].Name, 2, windowStart, windowEnd, 2, 3),
                    BuildPoseTrack(hostBridge.Units[2].Name, 3, windowStart, windowEnd, 4, 3),
                };
                if (!DriveTurn("one short track", 4, oneShort))
                {
                    return 1;
                }

                played = clientBridge.Played!;
                if (played.Poses.Count != 2
                    || FindPose(played.Poses, hostBridge.Units[1].Name) != null
                    || FindPose(played.Poses, hostBridge.Units[0].Name) == null
                    || FindPose(played.Poses, hostBridge.Units[2].Name) == null)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL a two-key track should be dropped alone, "
                        + $"but {played.Poses.Count} of 3 arrived");
                    return 1;
                }
                if (played.Tracks.Count != 3)
                {
                    Console.WriteLine("[selftest] FAIL dropping a pose track disturbed the transform tracks");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the unanimatable track alone was dropped");

                // --- turn 5: one unrepairable track demotes the whole turn.
                // All-or-nothing on purpose: one statue among walking mechs
                // reads as a broken game, where everyone sliding reads as the
                // lower-fidelity mode it is.
                var raggedSource = BuildPoseTrack(
                    hostBridge.Units[1].Name, 2, windowStart, windowEnd, 4, 3);
                var raggedKeys = new List<PoseKey>(raggedSource.Keys);
                raggedKeys[2] = new PoseKey(raggedKeys[2].Time, true, true, new[]
                {
                    new JointPose(new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f)),
                });
                var ragged = new List<UnitPoseTrack>
                {
                    BuildPoseTrack(hostBridge.Units[0].Name, 1, windowStart, windowEnd, 4, 3),
                    new UnitPoseTrack(raggedSource.Name, raggedSource.Joints, raggedKeys),
                    BuildPoseTrack(hostBridge.Units[2].Name, 3, windowStart, windowEnd, 4, 3),
                };
                if (!DriveTurn("one ragged track", 5, ragged))
                {
                    return 1;
                }

                played = clientBridge.Played!;
                if (played.Poses.Count != 0)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL a ragged track should demote the whole turn, "
                        + $"but {played.Poses.Count} tracks still played");
                    return 1;
                }
                if (played.Tracks.Count != 3)
                {
                    Console.WriteLine("[selftest] FAIL the demoted turn lost its transform tracks too");
                    return 1;
                }
                if (!hostLog.Saw($"turn 5 poses dropped: {PoseTrackFault.Ragged}"))
                {
                    Console.WriteLine("[selftest] FAIL the demotion was never explained in the log");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   one ragged track demoted the turn, transforms intact");

                // Three turns of pose faults and the session is still whole,
                // which is the assertion the over-cap turn exists for.
                if (clientSession.State != ClientSessionState.Planning || hostSession.Peers.Count != 1)
                {
                    Console.WriteLine("[selftest] FAIL the session did not survive the fault turns");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the session survived every pose fault");

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
