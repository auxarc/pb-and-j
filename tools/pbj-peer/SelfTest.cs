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
                ("pose fallbacks", RunPoseFallbacks),
                ("asset fallbacks", RunAssetFallbacks),
                ("remote guards", RunRemoteGuards),
                ("scenario transfer", RunScenarioTransfer),
                ("lobby barrier", RunLobbyBarrier),
                ("combat entry", RunCombatEntry),
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

                // M12b: entering combat no longer announces itself. The host is
                // ASKED to write the fight, says so once it has, then the offer
                // goes out, the client fetches and loads it, reports in, and only
                // then does CombatStart travel.
                //
                // Waiting for the ask rather than posting the answer cold is the
                // point: the harness has no game and no disk, so it cannot prove
                // the write -- but it can prove the ORDERING, which is the half
                // that cannot be checked without two people and a mission.
                if (!WaitFor("host was asked to write the fight",
                        () => hostBridge.ShipCombatRequested))
                {
                    return 1;
                }

                hostBridge.Scenario = new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2, 3, 4 }),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 5 }),
                });
                host.Post(new LocalCombatReadyEvent(
                    LobbySaveNames.ScenarioSlot, hostBridge.Scenario.Digest));

                if (!WaitFor("client fetched and loaded the host's fight",
                        () => clientBridge.CombatLoadRequested != null))
                {
                    return 1;
                }

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

        /// <summary>
        /// A synthetic turn of replayed effects, every value distinct. M14.
        /// </summary>
        /// <remarks>
        /// Distinctness for the reason <see cref="BuildPoseTrack"/> needs it, and
        /// more sharply: a projectile whose position and rotation were
        /// transposed flies sideways, and a standalone effect whose scale was
        /// lost renders at zero size — invisible, and indistinguishable from the
        /// feature not working. Neither shows in a count or a log line, so the
        /// comparison has to be field for field on values that cannot coincide.
        /// <para>
        /// The hue and colour blocks alternate present and absent across the
        /// standalone tracks on purpose. Absence is a real instruction — leave
        /// the prefab's own hue alone — and it is carried by a flag rather than
        /// a sentinel, so a codec that wrote the flag and forgot the payload
        /// would round-trip every present block and silently flatten the absent
        /// ones into zeroes.
        /// </para>
        /// </remarks>
        private static AssetCapture BuildAssetCapture(
            int seed,
            float windowStart,
            float windowEnd,
            int standaloneCount,
            int projectileCount,
            int beamCount,
            int keyCount = 3)
        {
            float At(int k) => keyCount > 1
                ? windowStart + ((windowEnd - windowStart) * k / (keyCount - 1f))
                : windowStart;

            var standalone = new StandaloneAssetTrack[standaloneCount];
            for (var i = 0; i < standaloneCount; i++)
            {
                var v = (seed * 1000f) + (i * 7f);
                standalone[i] = new StandaloneAssetTrack(
                    i,
                    new AssetTrackHead(
                        $"fx_impact_{seed}_{i}", windowStart + i, windowEnd + i,
                        i % 2 == 0 ? (float?)(i * 0.125f) : null,
                        i % 3 == 0
                            ? new AssetColour(
                                new Vec4(v, v + 1f, v + 2f, v + 3f),
                                new Vec4(v + 4f, v + 5f, v + 6f, v + 7f))
                            : (AssetColour?)null),
                    new Vec3(v, v + 0.25f, v + 0.5f),
                    UnitRotations[i % UnitRotations.Length],
                    new Vec3(v + 1f, v + 1.25f, v + 1.5f),
                    new Vec4(v + 2f, v + 2.25f, v + 2.5f, v + 2.75f),
                    new Vec3(v + 3f, v + 3.25f, v + 3.5f));
            }

            var projectiles = new ProjectileAssetTrack[projectileCount];
            for (var i = 0; i < projectileCount; i++)
            {
                var keys = new TransformKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    var v = (seed * 1000f) + (i * 7f) + (k * 0.75f);
                    keys[k] = new TransformKey(
                        At(k), new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(i + k) % UnitRotations.Length]);
                }
                projectiles[i] = new ProjectileAssetTrack(
                    i,
                    new AssetTrackHead($"fx_bullet_{seed}_{i}", windowStart, windowEnd + 1f),
                    new Vec3(1f + i, 2f + i, 3f + i),
                    keys);
            }

            var beams = new BeamAssetTrack[beamCount];
            for (var i = 0; i < beamCount; i++)
            {
                var keys = new BeamKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    var v = (seed * 1000f) + (i * 7f) + (k * 0.75f);
                    keys[k] = new BeamKey(
                        At(k), new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(i + k) % UnitRotations.Length],
                        new Vec3(v + 5f, v + 5.25f, v + 5.5f));
                }
                beams[i] = new BeamAssetTrack(
                    i, new AssetTrackHead($"fx_beam_{seed}_{i}", windowStart, windowEnd), keys);
            }

            return new AssetCapture(standalone, projectiles, beams);
        }

        /// <summary>
        /// Whether a turn's effects came back exactly as they went out.
        /// </summary>
        /// <remarks>
        /// Matched by id rather than by position, because the parts they
        /// travelled in are reassembled by concatenation and an ordering bug is
        /// one of the things this is here to catch — comparing positionally
        /// would make the assertion agree with the bug.
        /// </remarks>
        private static bool SameAssets(AssetCapture sent, AssetCapture got, out string why)
        {
            why = string.Empty;

            if (got.Standalone.Count != sent.Standalone.Count
                || got.Projectiles.Count != sent.Projectiles.Count
                || got.Beams.Count != sent.Beams.Count)
            {
                why = $"arrived as {got.Standalone.Count}/{got.Projectiles.Count}/{got.Beams.Count} "
                    + $"tracks, not {sent.Standalone.Count}/{sent.Projectiles.Count}/{sent.Beams.Count}";
                return false;
            }

            foreach (var a in sent.Standalone)
            {
                StandaloneAssetTrack? b = null;
                foreach (var candidate in got.Standalone)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"standalone {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"standalone {a.Id}", ref why)
                    || !SameVec3(a.Position, b.Position, $"standalone {a.Id} position", ref why)
                    || !SameVec4(a.Rotation, b.Rotation, $"standalone {a.Id} rotation", ref why)
                    || !SameVec3(a.Scale, b.Scale, $"standalone {a.Id} scale", ref why)
                    || !SameVec4(
                        a.VelocityAndDecay, b.VelocityAndDecay, $"standalone {a.Id} velocity", ref why)
                    || !SameVec3(
                        a.PositionLocal, b.PositionLocal, $"standalone {a.Id} local position", ref why))
                {
                    return false;
                }
            }

            foreach (var a in sent.Projectiles)
            {
                ProjectileAssetTrack? b = null;
                foreach (var candidate in got.Projectiles)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"projectile {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"projectile {a.Id}", ref why)
                    || !SameVec3(a.Scale, b.Scale, $"projectile {a.Id} scale", ref why))
                {
                    return false;
                }
                if (b.Keys.Count != a.Keys.Count)
                {
                    why = $"projectile {a.Id} arrived with {b.Keys.Count} keys, not {a.Keys.Count}";
                    return false;
                }
                for (var k = 0; k < a.Keys.Count; k++)
                {
                    if (a.Keys[k].Time != b.Keys[k].Time
                        || !SameVec3(
                            a.Keys[k].Position, b.Keys[k].Position,
                            $"projectile {a.Id} key {k} position", ref why)
                        || !SameVec4(
                            a.Keys[k].Rotation, b.Keys[k].Rotation,
                            $"projectile {a.Id} key {k} rotation", ref why))
                    {
                        if (why.Length == 0)
                        {
                            why = $"projectile {a.Id} key {k} is stamped {b.Keys[k].Time}";
                        }
                        return false;
                    }
                }
            }

            foreach (var a in sent.Beams)
            {
                BeamAssetTrack? b = null;
                foreach (var candidate in got.Beams)
                {
                    if (candidate.Id == a.Id)
                    {
                        b = candidate;
                    }
                }
                if (b == null)
                {
                    why = $"beam {a.Id} never arrived";
                    return false;
                }
                if (!SameHead(a.Head, b.Head, $"beam {a.Id}", ref why))
                {
                    return false;
                }
                if (b.Keys.Count != a.Keys.Count)
                {
                    why = $"beam {a.Id} arrived with {b.Keys.Count} keys, not {a.Keys.Count}";
                    return false;
                }
                for (var k = 0; k < a.Keys.Count; k++)
                {
                    if (a.Keys[k].Time != b.Keys[k].Time
                        || !SameVec3(
                            a.Keys[k].Position, b.Keys[k].Position,
                            $"beam {a.Id} key {k} position", ref why)
                        || !SameVec4(
                            a.Keys[k].Rotation, b.Keys[k].Rotation,
                            $"beam {a.Id} key {k} rotation", ref why)
                        || !SameVec3(
                            a.Keys[k].Parameters, b.Keys[k].Parameters,
                            $"beam {a.Id} key {k} parameters", ref why))
                    {
                        if (why.Length == 0)
                        {
                            why = $"beam {a.Id} key {k} is stamped {b.Keys[k].Time}";
                        }
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SameHead(AssetTrackHead a, AssetTrackHead b, string what, ref string why)
        {
            if (a.AssetKey != b.AssetKey)
            {
                why = $"{what} arrived keyed '{b.AssetKey}', not '{a.AssetKey}'";
                return false;
            }
            if (a.TimeStart != b.TimeStart || a.TimeEnd != b.TimeEnd)
            {
                why = $"{what} arrived spanning {b.TimeStart}..{b.TimeEnd}, not {a.TimeStart}..{a.TimeEnd}";
                return false;
            }

            // Absence and zero are different instructions, so HasValue is
            // compared before the value ever is.
            if (a.Hue.HasValue != b.Hue.HasValue
                || (a.Hue.HasValue && a.Hue!.Value != b.Hue!.Value))
            {
                why = $"{what} lost or invented a hue offset";
                return false;
            }
            if (a.Colour.HasValue != b.Colour.HasValue)
            {
                why = $"{what} lost or invented a colour";
                return false;
            }
            if (a.Colour.HasValue
                && (!SameVec4(a.Colour!.Value.From, b.Colour!.Value.From, $"{what} colour from", ref why)
                    || !SameVec4(a.Colour.Value.To, b.Colour.Value.To, $"{what} colour to", ref why)))
            {
                return false;
            }
            return true;
        }

        private static bool SameVec3(Vec3 a, Vec3 b, string what, ref string why)
        {
            if (a.X == b.X && a.Y == b.Y && a.Z == b.Z)
            {
                return true;
            }
            why = $"{what} became {b.X},{b.Y},{b.Z}, not {a.X},{a.Y},{a.Z}";
            return false;
        }

        private static bool SameVec4(Vec4 a, Vec4 b, string what, ref string why)
        {
            if (a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W)
            {
                return true;
            }
            why = $"{what} became {b.X},{b.Y},{b.Z},{b.W}, not {a.X},{a.Y},{a.Z},{a.W}";
            return false;
        }

        private static UnitPoseTrack? FindPose(IReadOnlyList<UnitPoseTrack> tracks, string? name)
        {
            foreach (var track in tracks)
            {
                if (track.Name == name)
                {
                    return track;
                }
            }
            return null;
        }

        /// <summary>
        /// A synthetic pose track whose every value is distinct.
        /// </summary>
        /// <remarks>
        /// Distinctness is the whole design of it. A codec that transposed two
        /// joints, two keys or two tracks would round-trip a track built from
        /// repeated values perfectly, and the wire assertions would pass while
        /// the client put a mech's elbow on its knee.
        /// <para>
        /// The last joint name deliberately repeats the one before it. Duplicate
        /// joint names are not a malformed input — a leg group appends its joints
        /// per leg from cloned prefabs, so every multi-legged unit carries them —
        /// and <see cref="PoseTracks.Remap"/> matches them ordinally. A harness
        /// that only ever sent unique names would leave that untested.
        /// </para>
        /// <para>
        /// Rotations come from the four axis-aligned unit quaternions rather than
        /// from anything computed, because the sampler normalises: a rotation
        /// that is only nearly unit-length would come back nearly equal, and this
        /// scenario compares exactly.
        /// </para>
        /// </remarks>
        private static UnitPoseTrack BuildPoseTrack(
            string? name, int seed, float windowStart, float windowEnd, int keyCount, int jointCount)
        {
            var joints = new string[jointCount];
            for (var j = 0; j < jointCount; j++)
            {
                joints[j] = j == jointCount - 1 && jointCount > 1
                    ? joints[j - 1]
                    : $"joint_{j}";
            }

            var keys = new PoseKey[keyCount];
            for (var k = 0; k < keyCount; k++)
            {
                var time = keyCount > 1
                    ? windowStart + ((windowEnd - windowStart) * k / (keyCount - 1f))
                    : windowStart;

                var poses = new JointPose[jointCount];
                for (var j = 0; j < jointCount; j++)
                {
                    var v = (seed * 100f) + (k * 10f) + (j * 0.5f);
                    poses[j] = new JointPose(
                        new Vec3(v, v + 0.25f, v + 0.5f),
                        UnitRotations[(k + j) % UnitRotations.Length]);
                }

                // Both flags vary, and independently: they pin the weapons to the
                // palms, so a codec that dropped or conflated one bit would leave
                // a rifle hanging in mid-air through the firing animation.
                keys[k] = new PoseKey(time, k % 2 == 0, k >= keyCount / 2, poses);
            }

            return new UnitPoseTrack(name, joints, keys);
        }

        private static readonly Vec4[] UnitRotations =
        {
            new Vec4(0f, 0f, 0f, 1f),
            new Vec4(0f, 1f, 0f, 0f),
            new Vec4(1f, 0f, 0f, 0f),
            new Vec4(0f, 0f, 1f, 0f),
        };

        /// <summary>
        /// Whether a pose track came back exactly as it went out.
        /// </summary>
        private static bool SamePoseTrack(UnitPoseTrack sent, UnitPoseTrack got, out string why)
        {
            why = string.Empty;

            if (got.Joints.Count != sent.Joints.Count)
            {
                why = $"arrived with {got.Joints.Count} joint names, not {sent.Joints.Count}";
                return false;
            }
            for (var j = 0; j < sent.Joints.Count; j++)
            {
                if (got.Joints[j] != sent.Joints[j])
                {
                    why = $"joint name {j} became '{got.Joints[j]}', not '{sent.Joints[j]}'";
                    return false;
                }
            }

            if (got.Keys.Count != sent.Keys.Count)
            {
                why = $"arrived with {got.Keys.Count} keys, not {sent.Keys.Count}";
                return false;
            }
            for (var k = 0; k < sent.Keys.Count; k++)
            {
                var a = sent.Keys[k];
                var b = got.Keys[k];
                if (a.Time != b.Time)
                {
                    why = $"key {k} is stamped {b.Time}, not {a.Time}";
                    return false;
                }
                if (a.SyncLeftEquipment != b.SyncLeftEquipment
                    || a.SyncRightEquipment != b.SyncRightEquipment)
                {
                    why = $"key {k} lost an equipment flag";
                    return false;
                }
                if (b.Joints.Count != a.Joints.Count)
                {
                    why = $"key {k} arrived with {b.Joints.Count} joints, not {a.Joints.Count}";
                    return false;
                }
                for (var j = 0; j < a.Joints.Count; j++)
                {
                    var from = a.Joints[j];
                    var to = b.Joints[j];
                    if (from.Position.X != to.Position.X || from.Position.Y != to.Position.Y
                        || from.Position.Z != to.Position.Z
                        || from.Rotation.X != to.Rotation.X || from.Rotation.Y != to.Rotation.Y
                        || from.Rotation.Z != to.Rotation.Z || from.Rotation.W != to.Rotation.W)
                    {
                        why = $"key {k} joint {j} changed crossing the wire";
                        return false;
                    }
                }
            }

            return true;
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

                // M11e: the host must actually hold the save it selects, or the
                // transfer has nothing to send and no peer can ever ready. The
                // digest is the payload's real one, because readying is gated on
                // holding *these* bytes and not merely a save of that name.
                var campaign = new ScenarioPayload("pbj_campaign", new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, System.Text.Encoding.UTF8.GetBytes("campaign")),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, System.Text.Encoding.UTF8.GetBytes("ver: 1")),
                });
                hostBridge.ScenariosByKey["pbj_campaign"] = campaign;

                host.Post(new LocalLobbySelectEvent("pbj_campaign", campaign.Digest));
                if (!WaitFor("the host's save reached the client",
                        () => clientSession.LobbySelectionVersion == 1
                              && clientSession.LobbySaveKey == "pbj_campaign"
                              && clientSession.LobbySaveDigest == campaign.Digest))
                {
                    return 1;
                }

                // The bytes cross before anyone can agree to load them. This is
                // M11e's whole shape: transfer on selection, so that by the time
                // the barrier fills every machine can genuinely load.
                if (!WaitFor("the campaign save crossed to the client",
                        () => clientBridge.WrittenScenarios.Count == 1
                              && clientBridge.WrittenScenarios[0].SaveName == "pbj_campaign"
                              && clientBridge.WrittenScenarios[0].Digest == campaign.Digest))
                {
                    return 1;
                }
                clientBridge.ScenariosByKey["pbj_campaign"] = campaign;

                host.Post(new LocalLobbyReadyEvent());
                if (!WaitFor("the host's own ready is not enough on its own",
                        () => hostSession.LobbyReadyCount == 1 && !hostSession.LobbyIsSatisfied))
                {
                    return 1;
                }

                client.Post(new LocalLobbyReadyEvent());

                // M11d: the barrier no longer STAYS filled. Filling it fires the
                // load, which spends the agreement — advancing the selection and
                // clearing every ready — so that a later barrier check on a
                // disconnect cannot re-fire and reload the campaign underneath
                // everyone. What proves the agreement happened is the load.
                if (!WaitFor("the agreement turned straight into a load",
                        () => hostSession.LoadInFlight))
                {
                    return 1;
                }

                // Both machines were told to load, and both were told the same
                // save. This is the whole M11d handshake over a real socket.
                if (!WaitFor("both machines began loading the chosen save",
                        () => hostBridge.LoadsBegun.Count == 1
                              && hostBridge.LoadsBegun[0] == "pbj_campaign"
                              && clientBridge.LoadsBegun.Count == 1
                              && clientBridge.LoadsBegun[0] == "pbj_campaign"))
                {
                    return 1;
                }

                // The client sees the spent agreement too: a new selection
                // version with nobody ready.
                if (!WaitFor("the client sees the agreement spent",
                        () => clientSession.LobbySelectionVersion == 2
                              && clientSession.LobbyRoster.Count == 2
                              && !clientSession.LobbyRoster[1].Ready))
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
        /// M12b: a peer that cannot get into the fight is dropped, and the fight
        /// starts without it.
        /// </summary>
        /// <remarks>
        /// The wedge this pins down cost nothing to write and would have cost an
        /// evening to find. Until M12b·2 a peer that reported a failed entry was
        /// dropped from the <em>entry</em> barrier and left in the registry — so
        /// it was still dealt units, and the <em>turn</em> barrier still waited on
        /// a ready it could never send, for every turn of a battle it was never
        /// in. Nobody could execute again, and the host had no way to tell why.
        /// <para>
        /// Over real sockets rather than in a unit test because the ordering is
        /// half the claim: the ask must arrive before anything is offered, and
        /// nothing may be announced until the entry barrier settles. The
        /// disconnect-mid-entry and exit-mid-entry cases live in
        /// <c>HostSessionTests</c> — killing a socket here would race the
        /// assertions, exactly as the lobby scenario notes.
        /// </para>
        /// </remarks>
        private static int RunCombatEntry()
        {
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
                if (!WaitFor("client handshaken", () => hostSession.Peers.Count == 1))
                {
                    return 1;
                }

                // The fight the host is about to enter, held by both sides, so
                // the client takes the load path rather than the fetch path --
                // the path with a refusal to report.
                var fight = new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 9, 8, 7 }),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 6 }),
                });
                hostBridge.Scenario = fight;
                clientBridge.Scenario = fight;
                clientBridge.CombatLoadRefusal = LoadOutcome.Unavailable;

                hostBridge.InCombat = true;
                hostBridge.CurrentTurn = 0;

                if (!WaitFor("host was asked to write the fight",
                        () => hostBridge.ShipCombatRequested))
                {
                    return 1;
                }

                if (clientSession.State == ClientSessionState.Planning)
                {
                    Console.WriteLine("[selftest] FAIL the fight was announced before it was shipped");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   nothing announced before the fight was written");

                host.Post(new LocalCombatReadyEvent(LobbySaveNames.ScenarioSlot, fight.Digest));

                if (!WaitFor("the peer that could not get in was dropped",
                        () => hostSession.Peers.Count == 0))
                {
                    return 1;
                }

                // The real assertion. A peer dropped from the entry barrier alone
                // would leave this at 2 for ever, and the next Execute would never
                // come.
                if (hostSession.ParticipantCount != 1)
                {
                    Console.WriteLine("[selftest] FAIL the turn barrier still waits for "
                        + hostSession.ParticipantCount + " machines");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the turn barrier waits only for the host");

                if (!hostLog.Saw("combat started"))
                {
                    Console.WriteLine("[selftest] FAIL the fight never started");
                    return 1;
                }
                Console.WriteLine("[selftest] OK   the fight started without it");

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
