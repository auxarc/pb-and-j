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
    /// The keyframe-stream scenario.
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
        /// A turn's motion crossing the wire and being reconstructed.
        /// </summary>
        /// <remarks>
        /// The tracks here are synthetic, and that is a real limit of this
        /// scenario: it pins the protocol, the codec and the sampler, but it
        /// cannot prove that a track built from the game's own
        /// <c>CombatReplayHelper</c> is correct. <c>pbj.replay-last</c> in the
        /// running game is the real-data half — it round-trips a genuine capture
        /// through this same codec before playing it. Neither gate is sufficient
        /// alone.
        /// <para>
        /// What it does prove is the invariant everything else rests on: the last
        /// key of every track lands exactly where the snapshot says the unit
        /// ended, so presenting the motion cannot fight the correction.
        /// </para>
        /// </remarks>
        private static int RunKeyframeStream()
        {
            var hostBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var host = new PbjRuntime(hostTransport, hostBridge, new PrefixedLog("host"), hostMailbox, hostSession);

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

            try
            {
                clientTransport.Connect("127.0.0.1", hostTransport.Port);
                if (!WaitFor("handshake completed",
                        () => clientSession.State == ClientSessionState.Planning && hostSession.Peers.Count == 1))
                {
                    return 1;
                }

                // A real commit first: the host only reports a turn complete
                // while it is executing one, so a scenario that skipped the
                // barrier would silently assert nothing.
                client.Post(new LocalReadyEvent());
                if (!WaitFor("host recorded the client's Ready", () => hostSession.ReadyCount == 1))
                {
                    return 1;
                }
                host.Post(new LocalReadyEvent());
                if (!WaitFor("turn committed and execution started",
                        () => hostSession.State == HostSessionState.Executing))
                {
                    return 1;
                }

                // Move the host's world, then build a track per unit that walks
                // from where it was to where it now is. The final key is read from
                // the same state the snapshot is — the invariant real capture
                // upholds by appending its last key from the snapshot's own read.
                const float windowStart = 15f;
                const float windowEnd = 20f;
                var start = new Vec3(0f, 0f, 0f);
                hostBridge.Units[0].Position = new Vec3(12.5f, 0f, -3.25f);
                hostBridge.Units[0].Rotation = new Vec4(0f, 0.70710678f, 0f, 0.70710678f);

                var tracks = new List<UnitTrack>();
                foreach (var unit in hostBridge.Units)
                {
                    tracks.Add(new UnitTrack(unit.Name, new[]
                    {
                        new TransformKey(windowStart, start, new Vec4(0f, 0f, 0f, 1f)),
                        new TransformKey((windowStart + windowEnd) / 2f,
                            new Vec3(unit.Position.X / 2f, unit.Position.Y / 2f, unit.Position.Z / 2f),
                            new Vec4(0f, 0f, 0f, 1f)),
                        new TransformKey(windowEnd, unit.Position, unit.Rotation),
                    }));
                }
                // M8's poses ride the same capture. They are a separate wire
                // message split one part per unit, and until this leg existed
                // nothing outside the game exercised that split at all — the
                // gate would have passed with the whole pose path broken.
                var poseTracks = new List<UnitPoseTrack>();
                for (var i = 0; i < hostBridge.Units.Count; i++)
                {
                    poseTracks.Add(BuildPoseTrack(
                        hostBridge.Units[i].Name, i + 1, windowStart, windowEnd, keyCount: 4, jointCount: 3));
                }
                // M14's effects ride the same capture and the same terminator.
                // Three kinds in one part here — the split itself is exercised
                // by the "asset fallbacks" scenario, which is where it can be
                // driven past a part boundary without inventing a 65-effect
                // turn in the middle of a motion test.
                var assetCapture = BuildAssetCapture(
                    seed: 1, windowStart, windowEnd,
                    standaloneCount: 4, projectileCount: 2, beamCount: 2);
                hostBridge.Keyframes = new KeyframeCapture(
                    windowStart, windowEnd, tracks, poseTracks, assetCapture);

                var hostDigest = hostBridge.ComputeStateDigest();
                var snapshot = hostBridge.CaptureSnapshot();
                host.Post(new LocalTurnCompleteEvent(hostDigest, snapshot, hostBridge.CaptureKeyframes()));

                if (!WaitFor("client received the turn's keyframes", () => clientBridge.Played != null))
                {
                    return 1;
                }

                var played = clientBridge.Played!;
                if (clientBridge.PlayedTurn != 3)
                {
                    Console.WriteLine($"[selftest] FAIL playback names turn {clientBridge.PlayedTurn}, not 3");
                    return 1;
                }
                if (played.WindowStart != windowStart || played.WindowEnd != windowEnd)
                {
                    Console.WriteLine("[selftest] FAIL the playback window did not survive the wire");
                    return 1;
                }
                if (played.Tracks.Count != tracks.Count)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL expected {tracks.Count} tracks, got {played.Tracks.Count}");
                    return 1;
                }

                for (var i = 0; i < tracks.Count; i++)
                {
                    var sent = tracks[i];
                    var got = played.Tracks[i];
                    if (got.Name != sent.Name || got.Transforms.Count != sent.Transforms.Count)
                    {
                        Console.WriteLine($"[selftest] FAIL track {i} lost its name or its keys");
                        return 1;
                    }
                    for (var k = 0; k < sent.Transforms.Count; k++)
                    {
                        var a = sent.Transforms[k];
                        var b = got.Transforms[k];
                        if (a.Time != b.Time
                            || a.Position.X != b.Position.X || a.Position.Y != b.Position.Y
                            || a.Position.Z != b.Position.Z
                            || a.Rotation.X != b.Rotation.X || a.Rotation.Y != b.Rotation.Y
                            || a.Rotation.Z != b.Rotation.Z || a.Rotation.W != b.Rotation.W)
                        {
                            Console.WriteLine($"[selftest] FAIL track {i} key {k} changed crossing the wire");
                            return 1;
                        }
                    }
                }
                Console.WriteLine($"[selftest] OK   {played.Tracks.Count} tracks survived the wire key for key");

                // The ordering proof, and the reason this assertion sits here
                // rather than in a wait of its own. The poses are already inside
                // the capture the terminator built, so they must have arrived
                // and been reassembled BEFORE the Keyframes message landed. Were
                // the send order reversed, the buffer would be empty at the
                // terminator and every part after it would be an orphan the
                // client can never resolve — and the count assertion below is
                // what makes that visible instead of silent.
                if (played.Poses.Count != poseTracks.Count)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL expected {poseTracks.Count} pose tracks reassembled, " +
                        $"got {played.Poses.Count}");
                    return 1;
                }

                for (var i = 0; i < poseTracks.Count; i++)
                {
                    var sent = poseTracks[i];
                    UnitPoseTrack? got = null;
                    foreach (var candidate in played.Poses)
                    {
                        if (candidate.Name == sent.Name)
                        {
                            got = candidate;
                        }
                    }
                    if (got == null)
                    {
                        Console.WriteLine($"[selftest] FAIL no pose track arrived for {sent.Name}");
                        return 1;
                    }
                    if (!SamePoseTrack(sent, got, out var why))
                    {
                        Console.WriteLine($"[selftest] FAIL pose track {sent.Name} {why}");
                        return 1;
                    }
                }
                Console.WriteLine(
                    $"[selftest] OK   {played.Poses.Count} pose tracks reassembled, joint for joint");

                // The sampler, on the data that actually crossed. Clamped at the
                // window's end to the final key, which is the pose invariant that
                // matches the transform one above: playback finishes in the pose
                // the host finished in, not part-way through a stride.
                var posed = played.Poses[0];
                if (!KeyframePlayback.TryBracket(posed, windowEnd, out var atEnd))
                {
                    Console.WriteLine("[selftest] FAIL the reassembled pose track would not bracket");
                    return 1;
                }
                var finalKey = posed.Keys[posed.Keys.Count - 1];
                KeyframePlayback.SampleJoint(atEnd, 0, out var jointEnd, out _);
                if (atEnd.T != 0f
                    || jointEnd.X != finalKey.Joints[0].Position.X
                    || jointEnd.Y != finalKey.Joints[0].Position.Y
                    || jointEnd.Z != finalKey.Joints[0].Position.Z
                    || atEnd.SyncLeftEquipment != finalKey.SyncLeftEquipment
                    || atEnd.SyncRightEquipment != finalKey.SyncRightEquipment)
                {
                    Console.WriteLine("[selftest] FAIL the pose at the window's end is not the final key");
                    return 1;
                }

                // And it interpolates rather than clamping everywhere, which is
                // the failure the check above cannot see on its own: a bracket
                // that always returned an endpoint would satisfy it and animate
                // nothing.
                var midway = (posed.Keys[0].Time + posed.Keys[1].Time) / 2f;
                if (!KeyframePlayback.TryBracket(posed, midway, out var atMid)
                    || atMid.T <= 0f || atMid.T >= 1f)
                {
                    Console.WriteLine("[selftest] FAIL a mid-span pose bracketed to an endpoint");
                    return 1;
                }
                KeyframePlayback.SampleJoint(atMid, 0, out var jointMid, out _);
                var low = posed.Keys[0].Joints[0].Position.X;
                var high = posed.Keys[1].Joints[0].Position.X;
                if (jointMid.X <= low || jointMid.X >= high)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL a joint sampled mid-span reads {jointMid.X}, outside ({low}, {high})");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   poses clamp to the final key and interpolate between");

                // M14, and the same ordering proof as the poses above: these are
                // already inside the capture the terminator built, so they must
                // have arrived and been reassembled before the Keyframes message
                // landed.
                if (!SameAssets(assetCapture, played.Assets, out var assetWhy))
                {
                    Console.WriteLine($"[selftest] FAIL replayed effects: {assetWhy}");
                    return 1;
                }
                Console.WriteLine(
                    $"[selftest] OK   {played.Assets.Standalone.Count} effects, "
                    + $"{played.Assets.Projectiles.Count} projectiles and "
                    + $"{played.Assets.Beams.Count} beams survived the wire field for field");

                // The activation arithmetic, on the data that actually crossed.
                // A point test here would look right and be wrong: a muzzle
                // flash lives under a tenth of a second and a frame is a
                // thirtieth, so a cursor sampled only at instants steps straight
                // over effects the host showed. Both are checked, because it is
                // the difference between them that is the design.
                var flash = played.Assets.Standalone[0];
                var brief = flash.Head.TimeStart + 0.001f;
                if (ReplayAssetPlayback.PhaseAt(flash.Head.TimeStart, brief, flash.Head.TimeStart - 1f)
                        != AssetTrackPhase.Pending
                    || ReplayAssetPlayback.PhaseAt(flash.Head.TimeStart, brief, flash.Head.TimeStart)
                        != AssetTrackPhase.Active
                    || ReplayAssetPlayback.PhaseAt(flash.Head.TimeStart, brief, brief + 1f)
                        != AssetTrackPhase.Expired)
                {
                    Console.WriteLine("[selftest] FAIL an effect's three phases are not distinguished");
                    return 1;
                }
                if (ReplayAssetPlayback.IsActiveAt(flash.Head.TimeStart, brief, brief + 0.5f)
                    || !ReplayAssetPlayback.CrossedDuring(
                        flash.Head.TimeStart, brief, flash.Head.TimeStart - 0.5f, brief + 0.5f))
                {
                    Console.WriteLine(
                        "[selftest] FAIL a sub-frame effect was stepped over — the interval test "
                        + "degraded to a point test");
                    return 1;
                }
                Console.WriteLine(
                    "[selftest] OK   a sub-frame effect is caught by the interval test a point test misses");

                // The load-bearing assertion: sampling at the end of the window
                // reproduces the snapshot exactly, so playback finishes where the
                // correction already put the unit.
                foreach (var unit in snapshot)
                {
                    UnitTrack? track = null;
                    foreach (var candidate in played.Tracks)
                    {
                        if (candidate.Name == unit.Name)
                        {
                            track = candidate;
                        }
                    }
                    if (track == null
                        || !KeyframePlayback.TrySample(track, windowEnd, out var end, out var rotation))
                    {
                        Console.WriteLine($"[selftest] FAIL no playable track for {unit.Name}");
                        return 1;
                    }
                    if (end.X != unit.Position.X || end.Y != unit.Position.Y || end.Z != unit.Position.Z
                        || rotation.X != unit.Rotation.X || rotation.Y != unit.Rotation.Y
                        || rotation.Z != unit.Rotation.Z || rotation.W != unit.Rotation.W)
                    {
                        Console.WriteLine(
                            $"[selftest] FAIL {unit.Name} ends playback at {end.X},{end.Y},{end.Z} " +
                            $"but the snapshot says {unit.Position.X},{unit.Position.Y},{unit.Position.Z}");
                        return 1;
                    }
                }
                Console.WriteLine("[selftest] OK   every track ends exactly where the snapshot says");

                // And it is motion, not a constant: without this the check above
                // would pass on a track that never moved at all.
                UnitTrack? mover = null;
                foreach (var candidate in played.Tracks)
                {
                    if (candidate.Name == hostBridge.Units[0].Name)
                    {
                        mover = candidate;
                    }
                }
                if (!KeyframePlayback.TrySample(mover, windowStart, out var began, out _)
                    || began.X == hostBridge.Units[0].Position.X)
                {
                    Console.WriteLine("[selftest] FAIL the moving unit's track is a constant, not a path");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   playback starts somewhere else and travels");

                // Correction and presentation agree, which is the whole reason
                // keyframes can be added without touching the snapshot path.
                if (clientBridge.ComputeStateDigest() != hostDigest)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL playback disturbed the correction: host {hostDigest}, " +
                        $"client {clientBridge.ComputeStateDigest()}");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   playback left the verified correction intact");

                // Combat ending mid-playback must stop it, or units keep sliding
                // along a finished turn's path into whatever comes next.
                hostBridge.InCombat = false;
                if (!WaitFor("combat ending stopped playback",
                        () => clientBridge.StopKeyframesCalls > 0 && clientBridge.Played == null))
                {
                    return 1;
                }

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
