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
    /// The scenario-transfer scenario.
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
    }
}
