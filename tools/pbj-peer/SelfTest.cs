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
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret");
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

                // An order the client owns, plus one it does not — the host must
                // apply the first and reject the second.
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
                Console.WriteLine("[selftest] OK   unowned order rejected, owned order applied");

                // The client must be told what became of its batch, by index.
                if (!WaitFor("client received its order result",
                        () => clientLog.Saw("turn 3 orders: 1 accepted, 1 rejected by host")))
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

                host.Post(new LocalTurnCompleteEvent(hostDigest, hostBridge.CaptureSnapshot()));

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
            var session = new HostSession("host", "selftest", 3, new ScriptedGameBridge(), "secret");
            var runtime = new PbjRuntime(transport, new ScriptedGameBridge(), new PrefixedLog("host"), mailbox, session);

            using (var raw = new TcpClient())
            {
                try
                {
                    raw.Connect(IPAddress.Loopback, transport.Port);
                    var stream = raw.GetStream();
                    stream.ReadTimeout = TimeoutSeconds * 1000;

                    // A protocol version the host cannot accept.
                    var hello = new HelloMessage(PbjProtocol.Magic, 999, "0.2.0", "stranger");
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
            var hostSession = new HostSession("host", "selftest", 3, hostBridge, "secret");
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
