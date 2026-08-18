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
    /// The scenarios about the link itself, and the helpers only they use.
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
    }
}
