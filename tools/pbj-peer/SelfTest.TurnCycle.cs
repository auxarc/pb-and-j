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
    // The turn-cycle scenario.
    //
    // One part of SelfTest, a single class split across files. Class-level
    // XML doc lives ONLY in SelfTest.cs: /// on a partial part is concatenated
    // by the compiler into one type entry, so eleven parts would produce
    // eleven summaries glued together. Caught by diffing the emitted XML.
    internal static partial class SelfTest
    {
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
                // M15. Two parts on one unit, one of them the spawn sentinel, so
                // the leg pins both the list layout and the sign that tells a
                // pre-battle wreck from one this turn produced.
                hostBridge.Units[2].WreckedParts = new[]
                {
                    new PartDestruction("equipment_left", 1.75f),
                    new PartDestruction("optional_right", -100f),
                };
                // The unit's own wreck travels beside the parts and is a
                // separate fact — a unit can be wrecked with parts surviving,
                // and lose every part without being wrecked itself.
                hostBridge.Units[2].IsWrecked = true;
                hostBridge.Units[2].WreckedAt = 1.75f;
                // M16. Two parts with integrity and barrier disagreeing in
                // opposite directions, so a codec that read one field into the
                // other cannot pass, plus the value a wrecked part really holds.
                hostBridge.Units[1].Parts = new[]
                {
                    new PartState("core", 0.25f, 0.75f),
                    new PartState("equipment_left", 0f, 0f),
                };
                // Presence is the field that had no carrier before M16, and
                // absence is what a host in combat actually reports for its whole
                // player squad. Set on a DIFFERENT unit from the one carrying the
                // parts, so neither can stand in for the other.
                hostBridge.Units[0].HasFrameIntegrity = false;
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
                    || corrected[2].WreckedParts.Count != 2
                    || corrected[2].WreckedParts[0].Socket != "equipment_left"
                    || corrected[2].WreckedParts[0].Time != 1.75f
                    || corrected[2].WreckedParts[1].Socket != "optional_right"
                    || corrected[2].WreckedParts[1].Time != -100f
                    || !corrected[2].IsWrecked
                    || corrected[2].WreckedAt != 1.75f
                    || corrected[0].IsWrecked)
                {
                    Console.WriteLine("[selftest] FAIL corrected state does not match field for field");
                    return 1;
                }
                Console.WriteLine(
                    "[selftest] OK   position, integrity, wrecked parts and the unit wreck all crossed intact");

                // M16, asserted separately from the block above so a failure says
                // which half moved. Both directions of the presence bit are
                // checked: unit 0 must have LOST it, and the others must keep it.
                if (corrected[1].Parts.Count != 2
                    || corrected[1].Parts[0].Socket != "core"
                    || corrected[1].Parts[0].Integrity != 0.25f
                    || corrected[1].Parts[0].Barrier != 0.75f
                    || corrected[1].Parts[1].Socket != "equipment_left"
                    || corrected[1].Parts[1].Integrity != 0f
                    || corrected[1].Parts[1].Barrier != 0f
                    || corrected[0].Parts.Count != 0
                    || corrected[0].HasFrameIntegrity
                    || !corrected[1].HasFrameIntegrity
                    || !corrected[2].HasFrameIntegrity)
                {
                    Console.WriteLine(
                        "[selftest] FAIL part state or frame-integrity presence did not cross intact");
                    return 1;
                }
                Console.WriteLine(
                    "[selftest] OK   per-part integrity, barrier and frame-integrity presence crossed intact");

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
    }
}
