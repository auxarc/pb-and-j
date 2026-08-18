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
    // The two scenarios about getting into a fight.
    //
    // One part of SelfTest, a single class split across files. Class-level
    // XML doc lives ONLY in SelfTest.cs: /// on a partial part is concatenated
    // by the compiler into one type entry, so eleven parts would produce
    // eleven summaries glued together. Caught by diffing the emitted XML.
    internal static partial class SelfTest
    {
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
                //
                // The version is 3, not 2, and getting that wrong made this leg
                // fail about one run in five. Selecting spends a version
                // (LobbyBarrier.cs:50 hands back Version + 1) and the load above
                // already spent one, so the client lands on 3. Asserting 2 here
                // inverted the race: WaitFor polls until its condition is TRUE,
                // so the leg passed only while the new LobbyState was still in
                // flight and failed — burning the whole 10s deadline — as soon
                // as it arrived. An assertion that holds only until the thing it
                // is testing happens is worse than no assertion, because it
                // reports the correct behaviour as the failure.
                host.Post(new LocalLobbySelectEvent("pbj_other", null));
                if (!WaitFor("changing the save cleared every ready",
                        () => !hostSession.LobbyIsSatisfied
                              && hostSession.LobbyReadyCount == 0
                              && clientSession.LobbySelectionVersion == 3
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
    }
}
