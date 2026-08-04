using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PBAndJ.Core.Net;
using PBAndJ.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// Runs a host and a client in one process over real loopback sockets and
    /// walks the whole turn cycle: connect, handshake, order, ready, commit,
    /// complete.
    /// </summary>
    /// <remarks>
    /// Not part of the 100% gate — it is an integration smoke test, not a unit
    /// test, and it involves real sockets and real timing. It gates
    /// <c>make deploy</c> instead, so a broken protocol can never reach the game.
    /// <para>
    /// Its value is that it exercises the same PbjRuntime, HostSession and
    /// ClientSession the game does, over the same transports, with no game
    /// installed.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class SelfTest
    {
        private const int TimeoutSeconds = 10;

        internal static int Run()
        {
            var scenarios = new (string Name, Func<int> Body)[]
            {
                ("turn cycle", RunTurnCycle),
                ("send order", RunSendOrder),
                ("backpressure", RunBackpressure),
                ("reconnect", RunReconnect),
                ("keyframe stream", RunKeyframeStream),
                ("remote guards", RunRemoteGuards),
                ("scenario transfer", RunScenarioTransfer),
                ("lobby barrier", RunLobbyBarrier),
            };

            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"[selftest] --- {scenario.Name} ---");
                var code = scenario.Body();
                if (code != 0)
                {
                    Console.WriteLine($"[selftest] FAILED in '{scenario.Name}'");
                    return code;
                }
            }

            Console.WriteLine("[selftest] ALL PASS");
            return 0;
        }

        private static int RunTurnCycle()
        {
            Console.WriteLine("[selftest] starting host and client over loopback");

            var hostBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var host = new PbjRuntime(hostTransport, hostBridge, new PrefixedLog("host"), hostMailbox, hostSession);
            Console.WriteLine($"[selftest] host listening on 127.0.0.1:{hostTransport.Port}");

            var clientBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var clientMailbox = new PbjMailbox(4096);
            var clientTransport = new TcpClientTransport(clientMailbox);
            var clientSession = new ClientSession("ally", "0.2.0", clientBridge);
            var clientLog = new PrefixedLog("ally");
            var client = new PbjRuntime(clientTransport, clientBridge, clientLog, clientMailbox, clientSession);

            // The clock the sessions see is separate from the wall clock the
            // waits use, so a scenario can advance it by 20 seconds without
            // waiting 20 seconds. Core never reads a clock, which is exactly
            // what makes this possible.
            var clock = Stopwatch.StartNew();
            var skew = 0.0;
            double Now() => clock.Elapsed.TotalSeconds + skew;

            void PumpBoth()
            {
                host.Pump(Now());
                client.Pump(Now());
            }

            bool WaitFor(string what, Func<bool> condition)
            {
                var deadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                while (clock.Elapsed.TotalSeconds < deadline)
                {
                    PumpBoth();
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

                if (clientSession.PeerId != 1)
                {
                    Console.WriteLine($"[selftest] FAIL expected peer id 1, got {clientSession.PeerId}");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   client assigned peer id 1");

                if (clientSession.Turn != 3)
                {
                    Console.WriteLine($"[selftest] FAIL expected turn 3, got {clientSession.Turn}");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   client learned the host's turn");

                var assigned = hostSession.Assignments.UnitsFor(1);
                if (assigned.Count == 0)
                {
                    Console.WriteLine("[selftest] FAIL client was assigned no units");
                    return 1;
                }
                Console.WriteLine($"[selftest] OK   client assigned {string.Join(", ", assigned)}");

                // An order the client owns, plus one it does not. Ownership is
                // enforced in two independent places and this exercises the outer
                // one: the client holds back what is not its own before sending.
                // The host's own check is the one that actually makes the rule
                // true — a peer that does not filter, or does not want to, is
                // still refused — but that cannot be shown from here precisely
                // because a well-behaved client no longer produces the case. It
                // is pinned in HostSessionTests instead, including the
                // OrderResult batch index and the NotOwned reason.
                clientBridge.Stage(Move(assigned[0]));
                clientBridge.Stage(Move(hostSession.Assignments.UnitsFor(0)[0]));
                client.Post(new LocalReadyEvent());

                if (!WaitFor("host recorded the client's Ready", () => hostSession.ReadyCount == 1))
                {
                    return 1;
                }

                // Un-ready must withdraw the submission without disturbing the
                // session, and re-readying must submit it again.
                client.Post(new LocalUnreadyEvent());
                if (!WaitFor("host cleared the client's readiness", () => hostSession.ReadyCount == 0))
                {
                    return 1;
                }

                client.Post(new LocalReadyEvent());
                if (!WaitFor("client re-readied after un-ready", () => hostSession.ReadyCount == 1))
                {
                    return 1;
                }

                host.Post(new LocalReadyEvent());

                if (!WaitFor("turn committed and broadcast",
                        () => hostSession.State == HostSessionState.Executing
                              && clientSession.State == ClientSessionState.Watching))
                {
                    return 1;
                }

                if (hostBridge.CommitTurnCalls != 1)
                {
                    Console.WriteLine($"[selftest] FAIL expected 1 commit, got {hostBridge.CommitTurnCalls}");
                    return 1;
                }
                if (hostBridge.AppliedOrders.Count != 1)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL expected 1 applied order, got {hostBridge.AppliedOrders.Count}");
                    return 1;
                }
                if (hostBridge.AppliedOrders[0].OwnerName != assigned[0])
                {
                    Console.WriteLine(
                        $"[selftest] FAIL the applied order was for {hostBridge.AppliedOrders[0].OwnerName}, " +
                        $"not the client's own {assigned[0]}");
                    return 1;
                }
                if (!clientLog.Saw("held back 1 order"))
                {
                    Console.WriteLine("[selftest] FAIL the client did not hold back the unowned order");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   unowned order held back, owned order applied");

                // The client must be told what became of its batch, by index.
                if (!WaitFor("client received its order result",
                        () => clientLog.Saw("turn 3 orders: 1 accepted, 0 rejected by host")))
                {
                    return 1;
                }

                // Move the host's world so the client is genuinely wrong about it,
                // then let the correction carry the truth across.
                hostBridge.Units[0].Position = new Vec3(12.5f, 0f, -3.25f);
                hostBridge.Units[1].Integrity = 0.5f;
                hostBridge.Units[2].IsDead = true;
                hostBridge.Units[2].DeathTime = 1.75f;
                var hostDigest = hostBridge.ComputeStateDigest();

                if (clientBridge.ComputeStateDigest() == hostDigest)
                {
                    Console.WriteLine("[selftest] FAIL client already matched — the test proves nothing");
                    return 1;
                }

                host.Post(new LocalTurnCompleteEvent(hostDigest, hostBridge.CaptureSnapshot(), hostBridge.CaptureKeyframes()));

                if (!WaitFor("turn completed and client back to planning",
                        () => clientSession.State == ClientSessionState.Planning && clientSession.Turn == 4))
                {
                    return 1;
                }

                // The M5 assertion: the client hard-set to the host's state and
                // its own recomputed digest now agrees.
                if (!WaitFor("client corrected itself to the host's state",
                        () => clientLog.Saw("corrected") && clientLog.Saw("OK")))
                {
                    return 1;
                }

                if (clientBridge.ComputeStateDigest() != hostDigest)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL digests still differ: host {hostDigest}, " +
                        $"client {clientBridge.ComputeStateDigest()}");
                    return 1;
                }

                var corrected = clientBridge.Units;
                if (corrected.Count != 3
                    || corrected[0].Position.X != 12.5f
                    || corrected[1].Integrity != 0.5f
                    || !corrected[2].IsDead
                    || corrected[2].DeathTime != 1.75f)
                {
                    Console.WriteLine("[selftest] FAIL corrected state does not match field for field");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   position, integrity and death all crossed intact");

                if (clientBridge.ClearLocalOrdersCalls == 0)
                {
                    Console.WriteLine("[selftest] FAIL stale local orders were never cleared");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   stale local orders cleared before the hard-set");

                // Combat edges are observed on the host's bridge and relayed, so
                // the client's own combat state is never consulted.
                hostBridge.InCombat = false;
                if (!WaitFor("client followed the host out of combat",
                        () => clientSession.State == ClientSessionState.Lobby && clientSession.OwnedUnits.Count == 0))
                {
                    return 1;
                }

                hostBridge.InCombat = true;
                hostBridge.CurrentTurn = 0;
                if (!WaitFor("client followed the host back into combat",
                        () => clientSession.State == ClientSessionState.Planning
                              && clientSession.Turn == 0
                              && clientSession.OwnedUnits.Count > 0))
                {
                    return 1;
                }

                // Keepalive: with the application layer completely silent — no
                // orders, no readies — the only thing keeping this connection
                // alive is Ping/Pong. Advance in ping-sized steps rather than one
                // jump, because a discontinuous jump past the timeout really is
                // that much silence and no Pong can travel back in time.
                var idleFor = 0.0;
                while (idleFor < PbjProtocol.PeerTimeoutSeconds * 2)
                {
                    skew += PbjProtocol.PingIntervalSeconds;
                    idleFor += PbjProtocol.PingIntervalSeconds;
                    for (var i = 0; i < 20; i++)
                    {
                        PumpBoth();
                        Thread.Sleep(1);
                    }
                }

                if (hostSession.Peers.Count != 1 || clientSession.State == ClientSessionState.Faulted)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL idle peer lost after {idleFor:F0}s despite keepalive " +
                        $"(peers {hostSession.Peers.Count}, client {clientSession.State})");
                    return 1;
                }
                Console.WriteLine($"[selftest] OK   keepalive held an idle peer for {idleFor:F0}s");

                // Now stop answering: with the client's pump frozen, the host
                // must reap it rather than wait forever.
                var frozenAt = Now();
                bool HostAloneAgain()
                {
                    host.Pump(frozenAt + PbjProtocol.PeerTimeoutSeconds + 1);
                    return hostSession.Peers.Count == 0;
                }

                var reaped = false;
                var reapDeadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                while (!reaped && clock.Elapsed.TotalSeconds < reapDeadline)
                {
                    reaped = HostAloneAgain();
                    Thread.Sleep(5);
                }
                if (!reaped)
                {
                    Console.WriteLine("[selftest] FAIL silent peer was never dropped");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   silent peer timed out and was dropped");

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

        /// <summary>
        /// The guards that only matter once a peer is not on this machine.
        /// </summary>
        /// <remarks>
        /// Every rejection here would otherwise surface as a session that
        /// connects fine and then diverges on every turn — the single worst thing
        /// to debug with a friend waiting at the other end of the country. Each
        /// one is checked over real sockets against the real host session.
        /// </remarks>
        private static int RunRemoteGuards()
        {
            var bridge = new ScriptedGameBridge();
            var mailbox = new PbjMailbox(4096);
            var transport = new TcpHostTransport(mailbox, IPAddress.Loopback, 0);
            transport.Start();
            var session = new HostSession(
                "host", "guards", 3, bridge, "secret",
                new SessionRequirements("0.3.0", "b8339", "hunter2"));
            var host = new PbjRuntime(transport, bridge, new PrefixedLog("host"), mailbox, session);

            var clock = Stopwatch.StartNew();
            var skew = 0.0;
            double Now() => clock.Elapsed.TotalSeconds + skew;

            // One connection attempt, pumped to completion, reporting whatever
            // the host said back.
            RejectReason? Attempt(string label, HelloMessage hello)
            {
                using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                peer.Connect(IPAddress.Loopback, transport.Port);
                var frame = FrameEncoder.Encode(PbjMessageCodec.Encode(hello));
                peer.Send(frame);

                var decoder = new FrameDecoder(PbjRuntime.MaxFrameLength);
                var buffer = new byte[4096];
                var deadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                while (clock.Elapsed.TotalSeconds < deadline)
                {
                    host.Pump(Now());
                    if (peer.Available > 0)
                    {
                        var read = peer.Receive(buffer);
                        foreach (var payload in decoder.Feed(buffer, 0, read))
                        {
                            switch (PbjMessageCodec.Decode(payload))
                            {
                                case RejectMessage reject:
                                    Console.WriteLine($"[selftest] OK   {label}: {reject.Reason}");
                                    return reject.Reason;
                                case WelcomeMessage:
                                    Console.WriteLine($"[selftest] OK   {label}: welcomed");
                                    return null;
                            }
                        }
                    }
                    Thread.Sleep(5);
                }
                Console.WriteLine($"[selftest] FAIL {label}: host never answered");
                return RejectReason.None;
            }

            HelloMessage Hello(
                string name, string mod = "0.3.0", string? build = "b8339", string? pass = "hunter2") =>
                new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, mod, name, build, pass);

            try
            {
                if (Attempt("wrong passphrase", Hello("a", pass: "letmein")) != RejectReason.BadPassphrase)
                {
                    return 1;
                }

                // Checked before anything else, so a caller that cannot get in
                // learns nothing about our build.
                if (Attempt("wrong passphrase AND wrong build", Hello("b", mod: "0.0.1", build: "b0", pass: "no"))
                    != RejectReason.BadPassphrase)
                {
                    return 1;
                }

                if (Attempt("wrong mod version", Hello("c", mod: "0.2.0")) != RejectReason.ModVersionMismatch)
                {
                    return 1;
                }

                if (Attempt("wrong game build", Hello("d", build: "b0001")) != RejectReason.GameBuildMismatch)
                {
                    return 1;
                }

                // A peer with no game to report is the harness itself, and has to
                // stay welcome — it is how every in-game gate is run.
                if (Attempt("no game build reported", Hello("e", build: null)) != null)
                {
                    return 1;
                }

                if (Attempt("everything matching", Hello("f")) != null)
                {
                    return 1;
                }

                // A socket that connects and says nothing must be reaped. Before
                // M7 nothing timed these out, which was survivable on loopback
                // and is not once the port is reachable.
                using (var mute = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    mute.Connect(IPAddress.Loopback, transport.Port);

                    // Let the accept actually land before moving the clock. The
                    // listener accepts on its own thread, so skewing first would
                    // start this socket's deadline from the post-skew instant and
                    // the test would wait forever for a drop that is not due yet.
                    var accepted = clock.Elapsed.TotalSeconds + 0.5;
                    while (clock.Elapsed.TotalSeconds < accepted)
                    {
                        host.Pump(Now());
                        Thread.Sleep(5);
                    }

                    skew += PbjProtocol.HandshakeTimeoutSeconds + 1;
                    var dropped = false;
                    var deadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                    while (!dropped && clock.Elapsed.TotalSeconds < deadline)
                    {
                        host.Pump(Now());
                        // Poll rather than read: a closed peer reports readable
                        // with nothing to read.
                        dropped = mute.Poll(1000, SelectMode.SelectRead) && mute.Available == 0;
                        Thread.Sleep(5);
                    }
                    if (!dropped)
                    {
                        Console.WriteLine("[selftest] FAIL a silent socket was never dropped");
                        return 1;
                    }
                    Console.WriteLine("[selftest] OK   a socket that never handshook was dropped");
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
                host.Stop();
            }
        }

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
                hostBridge.Keyframes = new KeyframeCapture(windowStart, windowEnd, tracks);

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

        /// <summary>
        /// M9: the host's combat save crosses the wire instead of a USB stick.
        /// </summary>
        /// <remarks>
        /// The whole point of stage 2 is that both machines hold byte-identical
        /// saves, because that is what makes their <c>nameInternal</c> join keys
        /// agree — which snapshot correction and keyframe playback are both built
        /// on. So this asserts byte equality, not merely that something arrived.
        /// <para>
        /// It also drives the refusals, because those are the paths where a
        /// peer's bytes were about to become files on someone's disk and the only
        /// thing stopping them is code nobody exercises by hand.
        /// </para>
        /// </remarks>
        private static int RunScenarioTransfer()
        {
            // Not in combat on either side: this is the main-menu case, which is
            // where a real session would do the transfer.
            var hostBridge = new ScriptedGameBridge { CurrentTurn = -1, InCombat = false };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var host = new PbjRuntime(hostTransport, hostBridge, new PrefixedLog("host"), hostMailbox, hostSession);

            var clientBridge = new ScriptedGameBridge { CurrentTurn = -1, InCombat = false };
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
                // A save big enough that a length or offset bug shows up as
                // corruption rather than passing by luck, with a byte pattern
                // that is not uniform.
                var content = new byte[64 * 1024];
                for (var i = 0; i < content.Length; i++)
                {
                    content[i] = (byte)((i * 31) & 0xFF);
                }
                hostBridge.Scenario = new ScenarioPayload("pbj_combat_test", new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, content),
                    new ScenarioFile(ScenarioPayload.MetadataFileName,
                        System.Text.Encoding.UTF8.GetBytes("ver: 1\nname: pbj_combat_test\n")),
                });

                clientTransport.Connect("127.0.0.1", hostTransport.Port);
                if (!WaitFor("handshake completed in the lobby",
                        () => clientSession.State == ClientSessionState.Lobby && hostSession.Peers.Count == 1))
                {
                    return 1;
                }

                // Offered, requested and delivered with no command typed: the
                // manual folder copy is what M9 removes.
                if (!WaitFor("client received the scenario unprompted",
                        () => clientBridge.WrittenScenarios.Count == 1))
                {
                    return 1;
                }

                var received = clientBridge.WrittenScenarios[0];
                if (received.Digest != hostBridge.Scenario.Digest)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL digest {received.Digest} does not match the host's " +
                        $"{hostBridge.Scenario.Digest}");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the received save digests to the host's");

                if (received.Files.Count != 2)
                {
                    Console.WriteLine($"[selftest] FAIL expected 2 files, got {received.Files.Count}");
                    return 1;
                }

                foreach (var sent in hostBridge.Scenario.Files)
                {
                    ScenarioFile? arrived = null;
                    foreach (var candidate in received.Files)
                    {
                        if (candidate.Name == sent.Name)
                        {
                            arrived = candidate;
                        }
                    }
                    if (arrived == null)
                    {
                        Console.WriteLine($"[selftest] FAIL {sent.Name} never arrived");
                        return 1;
                    }
                    if (arrived.Content.Length != sent.Content.Length)
                    {
                        Console.WriteLine(
                            $"[selftest] FAIL {sent.Name} arrived as {arrived.Content.Length} bytes, " +
                            $"sent {sent.Content.Length}");
                        return 1;
                    }
                    for (var i = 0; i < sent.Content.Length; i++)
                    {
                        if (arrived.Content[i] != sent.Content[i])
                        {
                            Console.WriteLine($"[selftest] FAIL {sent.Name} differs at byte {i}");
                            return 1;
                        }
                    }
                }
                Console.WriteLine(
                    $"[selftest] OK   {hostBridge.Scenario.TotalBytes:N0} bytes crossed byte for byte");

                // A second peer that already holds the save must cost nothing —
                // the case every reconnect hits.
                var secondBridge = new ScriptedGameBridge { CurrentTurn = -1, InCombat = false };
                secondBridge.Scenario = hostBridge.Scenario;
                var secondMailbox = new PbjMailbox(4096);
                var secondTransport = new TcpClientTransport(secondMailbox);
                var secondSession = new ClientSession("ally2", "0.2.0", secondBridge);
                var second = new PbjRuntime(
                    secondTransport, secondBridge, new PrefixedLog("ally2"), secondMailbox, secondSession);
                try
                {
                    secondTransport.Connect("127.0.0.1", hostTransport.Port);
                    var deadline = Now() + 2.0;
                    while (Now() < deadline)
                    {
                        host.Pump(Now());
                        client.Pump(Now());
                        second.Pump(Now());
                        Thread.Sleep(5);
                    }

                    if (secondSession.State != ClientSessionState.Lobby)
                    {
                        Console.WriteLine($"[selftest] FAIL second peer sits in {secondSession.State}");
                        return 1;
                    }
                    if (secondBridge.WrittenScenarios.Count != 0)
                    {
                        Console.WriteLine("[selftest] FAIL a peer that already held the save took it again");
                        return 1;
                    }
                    Console.WriteLine("[selftest] OK   a peer already holding the save transferred nothing");
                }
                finally
                {
                    second.Stop();
                }

                // pbj.scenario-pull: asks regardless of what is held.
                clientBridge.WrittenScenarios.Clear();
                client.Post(new LocalScenarioPullEvent());
                if (!WaitFor("a manual pull transferred it again",
                        () => clientBridge.WrittenScenarios.Count == 1))
                {
                    return 1;
                }

                // --- refusals: the paths where bytes were about to become files ---

                // A name that would escape the save directory.
                clientBridge.WrittenScenarios.Clear();
                clientSession.HandleMessage(ClientSession.HostConnectionId, new ScenarioMessage(
                    "evil", "whatever", new[]
                    {
                        new ScenarioFile("../../.bashrc", new byte[] { 1 }),
                        new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 2 }),
                    }));
                if (clientBridge.WrittenScenarios.Count != 0)
                {
                    Console.WriteLine("[selftest] FAIL a traversing file name was written");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   a traversing file name was refused");

                // Bigger than the cap.
                clientSession.HandleMessage(ClientSession.HostConnectionId, new ScenarioMessage(
                    "big", "whatever", new[]
                    {
                        new ScenarioFile(ScenarioPayload.ContentFileName,
                            new byte[ScenarioPayload.MaxTotalBytes]),
                        new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
                    }));
                if (clientBridge.WrittenScenarios.Count != 0)
                {
                    Console.WriteLine("[selftest] FAIL an oversized scenario was written");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   an oversized scenario was refused");

                // Bytes that do not match the digest claimed for them.
                clientSession.HandleMessage(ClientSession.HostConnectionId, new ScenarioMessage(
                    "pbj_combat_test", "deadbeef", hostBridge.Scenario.Files));
                if (clientBridge.WrittenScenarios.Count != 0)
                {
                    Console.WriteLine("[selftest] FAIL a scenario with a wrong digest was written");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   a mismatched digest was refused");

                // And none of that faulted the session.
                if (clientSession.State != ClientSessionState.Lobby)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL refusing a scenario faulted the session ({clientSession.State})");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   refusals left the session usable");

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

        /// <summary>
        /// Pins flush-before-close: a Reject must reach the peer before the FIN.
        /// </summary>
        /// <remarks>
        /// The disconnect that follows a Reject is queued behind it rather than
        /// closing the socket out of band. Without that, every rejection would
        /// arrive as a bare RST and a peer sending a bad protocol version would
        /// never learn why it was dropped.
        /// </remarks>
        private static int RunSendOrder()
        {
            var mailbox = new PbjMailbox(4096);
            var transport = new TcpHostTransport(mailbox, IPAddress.Loopback, 0);
            transport.Start();
            var session = new HostSession(
                "host", "selftest", 3, new ScriptedGameBridge(), "secret", SessionRequirements.None);
            var runtime = new PbjRuntime(transport, new ScriptedGameBridge(), new PrefixedLog("host"), mailbox, session);

            using (var raw = new TcpClient())
            {
                try
                {
                    raw.Connect(IPAddress.Loopback, transport.Port);
                    var stream = raw.GetStream();
                    stream.ReadTimeout = TimeoutSeconds * 1000;

                    // A protocol version the host cannot accept.
                    var hello = new HelloMessage(PbjProtocol.Magic, 999, "0.2.0", "stranger", null, null);
                    var frame = FrameEncoder.Encode(PbjMessageCodec.Encode(hello));
                    stream.Write(frame, 0, frame.Length);

                    var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
                    var decoder = new FrameDecoder(PbjRuntime.MaxFrameLength);
                    var buffer = new byte[4096];
                    RejectMessage? reject = null;

                    while (reject == null && DateTime.UtcNow < deadline)
                    {
                        runtime.Pump(0);
                        if (!raw.Client.Poll(5000, SelectMode.SelectRead))
                        {
                            continue;
                        }
                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            // FIN before the Reject — exactly the regression.
                            Console.WriteLine("[selftest] FAIL connection closed before the Reject arrived");
                            return 1;
                        }
                        foreach (var complete in decoder.Feed(buffer, 0, read))
                        {
                            reject = PbjMessageCodec.Decode(complete) as RejectMessage;
                        }
                    }

                    if (reject == null)
                    {
                        Console.WriteLine("[selftest] FAIL no Reject arrived");
                        return 1;
                    }
                    if (reject.Reason != RejectReason.VersionMismatch)
                    {
                        Console.WriteLine($"[selftest] FAIL expected VersionMismatch, got {reject.Reason}");
                        return 1;
                    }
                    Console.WriteLine("[selftest] OK   Reject delivered before the close");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[selftest] FAIL {e.GetType().Name}: {e.Message}");
                    return 1;
                }
                finally
                {
                    runtime.Stop();
                }
            }
            return 0;
        }

        /// <summary>
        /// Pins the backpressure policy and the absence of head-of-line blocking:
        /// a peer that stops reading is dropped, the pump never stalls, and the
        /// other peer is untouched.
        /// </summary>
        private static int RunBackpressure()
        {
            var mailbox = new PbjMailbox(65536);
            var transport = new TcpHostTransport(mailbox, IPAddress.Loopback, 0);
            transport.Start();

            var stalled = new TcpClient();
            var healthy = new TcpClient();
            try
            {
                stalled.Connect(IPAddress.Loopback, transport.Port);
                healthy.Connect(IPAddress.Loopback, transport.Port);

                var ids = WaitForPeerIds(mailbox, 2);
                if (ids.Count != 2)
                {
                    Console.WriteLine($"[selftest] FAIL expected 2 connections, saw {ids.Count}");
                    return 1;
                }
                var stalledId = ids[0];
                var healthyId = ids[1];

                // 64 KB per frame, so the 4 MB bound is reached in well under a
                // second even after the OS socket buffer has absorbed its share.
                var payload = FrameEncoder.Encode(new byte[64 * 1024]);
                var slowestSendMs = 0.0;
                var overflowed = false;
                var clock = Stopwatch.StartNew();

                for (var i = 0; i < 4096 && !overflowed && clock.Elapsed.TotalSeconds < TimeoutSeconds; i++)
                {
                    var before = clock.Elapsed.TotalMilliseconds;
                    transport.Send(stalledId, payload);
                    slowestSendMs = Math.Max(slowestSendMs, clock.Elapsed.TotalMilliseconds - before);

                    foreach (var evt in mailbox.DrainAll())
                    {
                        if (evt is TransportLogEvent log && log.Line != null && log.Line.Contains("OVERFLOWED"))
                        {
                            overflowed = true;
                        }
                    }
                }

                if (!overflowed)
                {
                    Console.WriteLine("[selftest] FAIL a peer that never reads was never dropped");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   stalled peer overflowed its queue and was dropped");

                // The whole point: queueing, not writing. Every Send must return
                // immediately even while that peer's socket is jammed.
                if (slowestSendMs > 100)
                {
                    Console.WriteLine($"[selftest] FAIL a Send blocked for {slowestSendMs:F0} ms");
                    return 1;
                }
                Console.WriteLine($"[selftest] OK   no Send blocked (slowest {slowestSendMs:F1} ms)");

                // No head-of-line blocking: the healthy peer is unaffected.
                var greeting = FrameEncoder.Encode(PbjMessageCodec.Encode(new TurnCommitMessage(7)));
                transport.Send(healthyId, greeting);

                healthy.GetStream().ReadTimeout = TimeoutSeconds * 1000;
                var decoder = new FrameDecoder(PbjRuntime.MaxFrameLength);
                var buffer = new byte[4096];
                var read = healthy.GetStream().Read(buffer, 0, buffer.Length);
                var frames = decoder.Feed(buffer, 0, read);
                if (frames.Count == 0 || !(PbjMessageCodec.Decode(frames[0]) is TurnCommitMessage commit) || commit.Turn != 7)
                {
                    Console.WriteLine("[selftest] FAIL the healthy peer did not receive its frame");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   healthy peer unaffected by the stalled one");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[selftest] FAIL {e.GetType().Name}: {e.Message}");
                return 1;
            }
            finally
            {
                stalled.Close();
                healthy.Close();
                transport.Stop();
            }
        }

        /// <summary>
        /// A player drops and comes back, and gets the same units under a new
        /// peer id — while a third party that tries to take its place does not.
        /// </summary>
        private static int RunReconnect()
        {
            var hostBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var host = new PbjRuntime(hostTransport, hostBridge, new PrefixedLog("host"), hostMailbox, hostSession);

            var clock = Stopwatch.StartNew();
            var skew = 0.0;
            double Now() => clock.Elapsed.TotalSeconds + skew;

            PbjRuntime? client = null;
            TcpClientTransport? clientTransport = null;

            void PumpAll()
            {
                host.Pump(Now());
                client?.Pump(Now());
            }

            bool WaitFor(string what, Func<bool> condition)
            {
                var deadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                while (clock.Elapsed.TotalSeconds < deadline)
                {
                    PumpAll();
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
                var firstBridge = new ScriptedGameBridge { CurrentTurn = 3 };
                var firstMailbox = new PbjMailbox(4096);
                clientTransport = new TcpClientTransport(firstMailbox);
                var firstSession = new ClientSession("ally", "0.2.0", firstBridge);
                client = new PbjRuntime(clientTransport, firstBridge, new PrefixedLog("ally"), firstMailbox, firstSession);
                clientTransport.Connect("127.0.0.1", hostTransport.Port);

                if (!WaitFor("first connection handshook", () => hostSession.Peers.Count == 1 && firstSession.PeerId == 1))
                {
                    return 1;
                }

                var heldUnits = new List<string>(hostSession.Assignments.UnitsFor(1));
                var token = firstSession.ResumeToken;
                var sessionId = firstSession.SessionId;
                if (token == null || heldUnits.Count == 0)
                {
                    Console.WriteLine("[selftest] FAIL no resume token or no units to reclaim");
                    return 1;
                }
                Console.WriteLine($"[selftest] OK   issued a resume token for {string.Join(", ", heldUnits)}");

                // Drop it.
                clientTransport.Stop();
                client = null;
                if (!WaitFor("host noticed the drop", () => hostSession.Peers.Count == 0))
                {
                    return 1;
                }

                // The units must NOT have been re-dealt to the host.
                if (hostSession.Assignments.UnitsFor(1).Count != heldUnits.Count)
                {
                    Console.WriteLine("[selftest] FAIL units were reassigned instead of held");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   units held rather than reassigned");

                // An impostor must not be able to take the name while it is held.
                {
                    var impostorBridge = new ScriptedGameBridge();
                    var impostorSession = new ClientSession("ally", "0.2.0", impostorBridge);
                    var impostorMailbox = new PbjMailbox(256);
                    var impostorTransport = new TcpClientTransport(impostorMailbox);
                    var impostorRuntime = new PbjRuntime(
                        impostorTransport, impostorBridge, new PrefixedLog("impostor"), impostorMailbox, impostorSession);
                    impostorTransport.Connect("127.0.0.1", hostTransport.Port);

                    var refused = false;
                    var deadline = clock.Elapsed.TotalSeconds + TimeoutSeconds;
                    while (!refused && clock.Elapsed.TotalSeconds < deadline)
                    {
                        host.Pump(Now());
                        impostorRuntime.Pump(Now());
                        refused = impostorSession.State == ClientSessionState.Faulted;
                        Thread.Sleep(5);
                    }
                    impostorRuntime.Stop();

                    if (!refused)
                    {
                        Console.WriteLine("[selftest] FAIL an impostor took a held player's name");
                        return 1;
                    }
                    Console.WriteLine("[selftest] OK   impostor refused while the name was held");
                }

                // Come back with the token.
                var secondBridge = new ScriptedGameBridge { CurrentTurn = 3 };
                var secondMailbox = new PbjMailbox(4096);
                clientTransport = new TcpClientTransport(secondMailbox);
                var secondSession = new ClientSession("ally", "0.2.0", secondBridge, sessionId, 1, token);
                client = new PbjRuntime(
                    clientTransport, secondBridge, new PrefixedLog("ally"), secondMailbox, secondSession);
                clientTransport.Connect("127.0.0.1", hostTransport.Port);

                if (!WaitFor("rejoined with a new peer id", () => secondSession.PeerId > 1))
                {
                    return 1;
                }

                var reclaimed = hostSession.Assignments.UnitsFor(secondSession.PeerId);
                if (reclaimed.Count != heldUnits.Count)
                {
                    Console.WriteLine(
                        $"[selftest] FAIL expected {heldUnits.Count} units back, got {reclaimed.Count}");
                    return 1;
                }
                for (var i = 0; i < heldUnits.Count; i++)
                {
                    if (reclaimed[i] != heldUnits[i])
                    {
                        Console.WriteLine($"[selftest] FAIL expected {heldUnits[i]}, got {reclaimed[i]}");
                        return 1;
                    }
                }
                Console.WriteLine(
                    $"[selftest] OK   reclaimed {string.Join(", ", reclaimed)} as peer #{secondSession.PeerId}");

                if (hostSession.Assignments.UnitsFor(1).Count != 0)
                {
                    Console.WriteLine("[selftest] FAIL the old peer id still owns units");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the old peer id owns nothing");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[selftest] FAIL {e.GetType().Name}: {e.Message}");
                return 1;
            }
            finally
            {
                client?.Stop();
                clientTransport?.Stop();
                host.Stop();
            }
        }

        private static List<int> WaitForPeerIds(PbjMailbox mailbox, int count)
        {
            var ids = new List<int>();
            var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
            while (ids.Count < count && DateTime.UtcNow < deadline)
            {
                foreach (var evt in mailbox.DrainAll())
                {
                    if (evt is PeerConnectedEvent connected)
                    {
                        ids.Add(connected.PeerId);
                    }
                }
                Thread.Sleep(5);
            }
            ids.Sort();
            return ids;
        }

        private static OrderPayload Move(string unit) =>
            new OrderPayload("move_run", unit, 0f, 2f,
                pathPoints: new[] { new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f) },
                pathLinks: new[] { new PathLink(0, 0) });

        /// <summary>
        /// M11a end to end: the host picks a save, everyone agrees, and changing
        /// the save takes every agreement back.
        /// </summary>
        /// <remarks>
        /// This scenario is the reason M11a does not ship inert. Nothing in the
        /// game acts on the lobby barrier yet — M11d owns that — so every unit
        /// test could pass with the wire never actually carrying a lobby, which
        /// is exactly how M6 shipped a feature that had never run. Here it runs:
        /// real sockets, the real codec, the same PbjRuntime the game pumps.
        /// </remarks>
        private static int RunLobbyBarrier()
        {
            // Out of combat on both sides — the lobby is the main-menu case.
            var hostBridge = new ScriptedGameBridge { CurrentTurn = -1, InCombat = false };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret", SessionRequirements.None);
            var hostLog = new PrefixedLog("host");
            var host = new PbjRuntime(hostTransport, hostBridge, hostLog, hostMailbox, hostSession);

            var clientBridge = new ScriptedGameBridge { CurrentTurn = -1, InCombat = false };
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

                // The roster reaches the client on handshake, before any save is
                // picked — nothing is selected yet and that is a real state, not
                // a missing one.
                if (!WaitFor("client received the lobby on handshake",
                        () => clientSession.LobbyRoster.Count == 2
                              && clientSession.LobbySelectionVersion == 0
                              && clientSession.LobbySaveKey == null))
                {
                    return 1;
                }

                // Readying for nothing is refused, on the client, before a byte
                // is spent.
                client.Post(new LocalLobbyReadyEvent());
                if (!WaitFor("a ready with no save selected went nowhere",
                        () => !clientSession.LobbyReadySent && hostSession.LobbyReadyCount == 0))
                {
                    return 1;
                }

                host.Post(new LocalLobbySelectEvent("pbj_campaign", "3f9c1a04"));
                if (!WaitFor("the host's save reached the client",
                        () => clientSession.LobbySelectionVersion == 1
                              && clientSession.LobbySaveKey == "pbj_campaign"
                              && clientSession.LobbySaveDigest == "3f9c1a04"))
                {
                    return 1;
                }

                host.Post(new LocalLobbyReadyEvent());
                if (!WaitFor("the host's own ready is not enough on its own",
                        () => hostSession.LobbyReadyCount == 1 && !hostSession.LobbyIsSatisfied))
                {
                    return 1;
                }

                client.Post(new LocalLobbyReadyEvent());
                if (!WaitFor("the barrier filled once the client agreed",
                        () => hostSession.LobbyIsSatisfied && hostSession.LobbyReadyCount == 2))
                {
                    return 1;
                }

                // The client learns the whole roster's readiness, not just its
                // own — this is what a lobby screen renders.
                if (!WaitFor("the client sees everyone ready",
                        () => clientSession.LobbyRoster.Count == 2
                              && clientSession.LobbyRoster[0].Ready
                              && clientSession.LobbyRoster[1].Ready))
                {
                    return 1;
                }

                // Changing the save must take every agreement back, on both
                // sides, or somebody loads a save they never confirmed.
                host.Post(new LocalLobbySelectEvent("pbj_other", null));
                if (!WaitFor("changing the save cleared every ready",
                        () => !hostSession.LobbyIsSatisfied
                              && hostSession.LobbyReadyCount == 0
                              && clientSession.LobbySelectionVersion == 2
                              && !clientSession.LobbyReadySent))
                {
                    return 1;
                }

                // A ready for the save that was just replaced must be recognised
                // and dropped, not counted toward the new one.
                clientTransport.Send(ClientSession.HostConnectionId,
                    FrameEncoder.Encode(PbjMessageCodec.Encode(new LobbyReadyMessage(1))));
                if (!WaitFor("a ready for the previous save was ignored",
                        () => hostLog.Saw("the save has changed since") && hostSession.LobbyReadyCount == 0))
                {
                    return 1;
                }

                // And the lobby still works afterwards: a stale ready is a
                // recoverable annoyance, not a broken session.
                client.Post(new LocalLobbyReadyEvent());
                host.Post(new LocalLobbyReadyEvent());
                if (!WaitFor("the lobby still fills after a stale ready",
                        () => hostSession.LobbyIsSatisfied))
                {
                    return 1;
                }

                // And a satisfied barrier can be un-satisfied again: agreement is
                // withdrawable right up until M11d acts on it. (The other way a
                // barrier fills — the last unready member simply leaving — is
                // driven in HostSessionTests, since killing a socket here would
                // race the assertions.)
                host.Post(new LocalLobbyUnreadyEvent());
                if (!WaitFor("the host withdrew its own agreement",
                        () => !hostSession.LobbyIsSatisfied))
                {
                    return 1;
                }

                Console.WriteLine("[selftest] PASS");
                return 0;
            }
            finally
            {
                client.Stop();
                host.Stop();
            }
        }

        /// <summary>
        /// Echoes and remembers. Remembering is what lets the self-test assert on
        /// messages the session reports but does not expose as state.
        /// </summary>
        private sealed class PrefixedLog : IPbjLog
        {
            private readonly string who;
            private readonly List<string> lines = new List<string>();

            public PrefixedLog(string who) => this.who = who;

            public void Log(string line)
            {
                lines.Add(line);
                Console.WriteLine($"  [{who}] {line}");
            }

            public bool Saw(string fragment) => lines.Exists(l => l.Contains(fragment));
        }
    }
}
