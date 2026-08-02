using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
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
            Console.WriteLine("[selftest] starting host and client over loopback");

            var hostBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var hostMailbox = new PbjMailbox(4096);
            var hostTransport = new TcpHostTransport(hostMailbox, IPAddress.Loopback, 0);
            hostTransport.Start();
            var hostSession = new HostSession("host", "selftest", 3, hostBridge);
            var host = new PbjRuntime(hostTransport, hostBridge, new PrefixedLog("host"), hostMailbox, hostSession);
            Console.WriteLine($"[selftest] host listening on 127.0.0.1:{hostTransport.Port}");

            var clientBridge = new ScriptedGameBridge { CurrentTurn = 3 };
            var clientMailbox = new PbjMailbox(4096);
            var clientTransport = new TcpClientTransport(clientMailbox);
            var clientSession = new ClientSession("ally", "0.2.0", clientBridge);
            var client = new PbjRuntime(clientTransport, clientBridge, new PrefixedLog("ally"), clientMailbox, clientSession);

            var clock = Stopwatch.StartNew();
            void PumpBoth()
            {
                host.Pump(clock.Elapsed.TotalSeconds);
                client.Pump(clock.Elapsed.TotalSeconds);
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

                host.Post(new LocalTurnCompleteEvent(hostBridge.ComputeStateDigest()));

                if (!WaitFor("turn completed and client back to planning",
                        () => clientSession.State == ClientSessionState.Planning && clientSession.Turn == 4))
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

        private static OrderPayload Move(string unit) =>
            new OrderPayload("move_run", unit, 0f, 2f,
                pathPoints: new[] { new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f) },
                pathLinks: new[] { new PathLink(0, 0) });

        private sealed class PrefixedLog : IPbjLog
        {
            private readonly string who;

            public PrefixedLog(string who) => this.who = who;

            public void Log(string line) => Console.WriteLine($"  [{who}] {line}");
        }
    }
}
