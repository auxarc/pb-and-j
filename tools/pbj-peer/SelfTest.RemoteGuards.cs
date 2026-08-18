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
    // The remote-guard scenario.
    //
    // One part of SelfTest, a single class split across files. Class-level
    // XML doc lives ONLY in SelfTest.cs: /// on a partial part is concatenated
    // by the compiler into one type entry, so eleven parts would produce
    // eleven summaries glued together. Caught by diffing the emitted XML.
    internal static partial class SelfTest
    {
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
    }
}
