using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Threading;
using PBAndJ.Core.Net;
using PBAndJ.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// Standalone peer that speaks the pb-and-j protocol using the same
    /// PBAndJ.Core assembly the mod does — so a running game can be tested
    /// against a real second peer without a second copy of the game.
    /// </summary>
    [ExcludeFromCodeCoverage]
    internal static class Program
    {
        private const int PumpIntervalMs = 16;

        private static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";
            switch (mode)
            {
                case "connect":
                    return Connect(Options.Parse(args));
                case "listen":
                    return Listen(Options.Parse(args));
                case "selftest":
                    return SelfTest.Run();
                default:
                    Console.WriteLine(Usage);
                    return mode == "help" ? 0 : 2;
            }
        }

        private const string Usage =
            "pbj-peer — standalone peer for the pb-and-j protocol\n" +
            "\n" +
            "  pbj-peer connect --host 127.0.0.1 --port 27600 --name ally [--protocol N] [--auto-ready]\n" +
            "  pbj-peer listen  --bind 127.0.0.1 --port 27600 --name host [--peers 3]\n" +
            "  pbj-peer selftest\n" +
            "\n" +
            "REPL commands: status, units, order <unit> <x> <y> <z>, orders, clear, ready, digest, quit";

        private static int Connect(Options options)
        {
            if (options.ProtocolVersion.HasValue)
            {
                return RawHandshake(options);
            }

            var bridge = new ScriptedGameBridge();
            var mailbox = new PbjMailbox(4096);
            var transport = new TcpClientTransport(mailbox);
            var session = new ClientSession(options.Name, options.ModVersion, bridge);
            var runtime = new PbjRuntime(transport, bridge, new ConsoleLog(), mailbox, session);

            Console.WriteLine(NetLog.ClientConnecting(options.Host, options.Port, options.Name));
            try
            {
                transport.Connect(options.Host, options.Port);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[pbj-peer] could not connect: {e.GetType().Name}: {e.Message}");
                return 1;
            }

            return RunRepl(runtime, bridge, () => session.State switch
            {
                ClientSessionState.Faulted => 1,
                ClientSessionState.Closed => 0,
                _ => (int?)null,
            }, options);
        }

        /// <summary>
        /// Sends a Hello with a deliberately wrong protocol version and reports
        /// the host's Reject. Bypasses ClientSession on purpose: the real
        /// session must always send the real version, so the override lives here
        /// in the harness rather than becoming a settable field in Core.
        /// </summary>
        private static int RawHandshake(Options options)
        {
            var version = options.ProtocolVersion!.Value;
            Console.WriteLine($"[pbj-peer] raw handshake to {options.Host}:{options.Port} claiming protocol v{version}");

            using var client = new System.Net.Sockets.TcpClient { NoDelay = true, ReceiveTimeout = 5000 };
            try
            {
                client.Connect(options.Host, options.Port);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[pbj-peer] could not connect: {e.GetType().Name}: {e.Message}");
                return 1;
            }

            var stream = client.GetStream();
            var hello = FrameEncoder.Encode(PbjMessageCodec.Encode(
                new HelloMessage(PbjProtocol.Magic, version, options.ModVersion, options.Name)));
            stream.Write(hello, 0, hello.Length);

            var decoder = new FrameDecoder(PbjRuntime.MaxFrameLength);
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    foreach (var payload in decoder.Feed(buffer, 0, read))
                    {
                        var message = PbjMessageCodec.Decode(payload);
                        if (message is RejectMessage reject)
                        {
                            Console.WriteLine($"[pbj-peer] rejected: {reject.Reason} ({reject.Detail ?? "no detail"})");
                            return 3;
                        }
                        Console.WriteLine($"[pbj-peer] unexpected {message.Type} — host accepted a bad version");
                        return 1;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[pbj-peer] read failed: {e.GetType().Name}: {e.Message}");
            }

            Console.WriteLine("[pbj-peer] host closed without sending Reject");
            return 1;
        }

        private static int Listen(Options options)
        {
            var bridge = new ScriptedGameBridge();
            var mailbox = new PbjMailbox(4096);
            var transport = new TcpHostTransport(mailbox, IPAddress.Parse(options.Bind), options.Port);
            transport.Start();

            var session = new HostSession(options.Name, "peer01", options.Peers, bridge);
            var runtime = new PbjRuntime(transport, bridge, new ConsoleLog(), mailbox, session);

            Console.WriteLine(NetLog.HostListening(options.Bind, transport.Port, PbjProtocol.Version, options.Peers));
            return RunRepl(runtime, bridge, () => session.State == HostSessionState.Closed ? 0 : (int?)null, options);
        }

        /// <summary>
        /// Pumps on a background thread while the foreground reads stdin, so you
        /// can type at the harness with the game window up.
        /// </summary>
        private static int RunRepl(PbjRuntime runtime, ScriptedGameBridge bridge, Func<int?> exitCode, Options options)
        {
            var clock = Stopwatch.StartNew();
            var exit = new ManualResetEventSlim(false);
            var result = 0;

            var pump = new Thread(() =>
            {
                while (!exit.IsSet)
                {
                    runtime.Pump(clock.Elapsed.TotalSeconds);
                    var code = exitCode();
                    if (code.HasValue)
                    {
                        result = code.Value;
                        exit.Set();
                        return;
                    }
                    Thread.Sleep(PumpIntervalMs);
                }
            })
            { IsBackground = true, Name = "pbj-peer-pump" };
            pump.Start();

            while (!exit.IsSet)
            {
                var line = Console.ReadLine();
                if (line == null)
                {
                    break;
                }
                if (!HandleCommand(line.Trim(), runtime, bridge))
                {
                    break;
                }
            }

            exit.Set();
            runtime.Stop();
            return result;
        }

        private static bool HandleCommand(string line, PbjRuntime runtime, ScriptedGameBridge bridge)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return true;
            }

            switch (parts[0])
            {
                case "quit":
                case "exit":
                    return false;

                case "status":
                    Console.WriteLine(Describe(runtime));
                    break;

                case "orders":
                    if (bridge.StagedOrders.Count == 0)
                    {
                        Console.WriteLine("[pbj-peer] no staged orders");
                    }
                    foreach (var order in bridge.StagedOrders)
                    {
                        Console.WriteLine($"[pbj-peer]   {order.OwnerName}: {order.Blueprint}");
                    }
                    break;

                case "clear":
                    bridge.ClearStaged();
                    Console.WriteLine("[pbj-peer] staged orders cleared");
                    break;

                case "order":
                    StageOrder(parts, bridge);
                    break;

                case "ready":
                    runtime.Post(new LocalReadyEvent());
                    Console.WriteLine("[pbj-peer] ready posted");
                    break;

                case "units":
                    if (runtime.Session is ClientSession session)
                    {
                        Console.WriteLine(session.OwnedUnits.Count == 0
                            ? "[pbj-peer] no units assigned yet"
                            : $"[pbj-peer] you control: {string.Join(", ", session.OwnedUnits)}");
                    }
                    break;

                case "digest":
                    Console.WriteLine($"[pbj-peer] local digest {bridge.ComputeStateDigest()}");
                    break;

                default:
                    Console.WriteLine(Usage);
                    break;
            }
            return true;
        }

        private static void StageOrder(string[] parts, ScriptedGameBridge bridge)
        {
            if (parts.Length < 5)
            {
                Console.WriteLine("[pbj-peer] usage: order <unit> <x> <y> <z>");
                return;
            }
            if (!TryFloat(parts[2], out var x) || !TryFloat(parts[3], out var y) || !TryFloat(parts[4], out var z))
            {
                Console.WriteLine("[pbj-peer] coordinates must be numbers");
                return;
            }

            var points = new[] { new Vec3(0f, 0f, 0f), new Vec3(x, y, z) };
            var links = new[] { new PathLink(0, 0) };
            bridge.Stage(new OrderPayload("move_run", parts[1], 0f, 2f, pathPoints: points, pathLinks: links));
            Console.WriteLine($"[pbj-peer] staged move_run for {parts[1]} -> ({x}, {y}, {z})");
        }

        private static bool TryFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string Describe(PbjRuntime runtime)
        {
            if (runtime.Session is HostSession host)
            {
                return NetLog.Status("HOST", host.State.ToString(), host.Turn, host.ParticipantCount, host.ReadyCount);
            }
            var client = (ClientSession)runtime.Session;
            return NetLog.Status("CLIENT", client.State.ToString(), client.Turn, 1, 0);
        }

        private sealed class Options
        {
            public string Host { get; private set; } = "127.0.0.1";
            public string Bind { get; private set; } = "127.0.0.1";
            public int Port { get; private set; } = 27600;
            public string Name { get; private set; } = "ally";
            public int Peers { get; private set; } = 3;
            public string ModVersion { get; private set; } = "0.2.0";
            public bool AutoReady { get; private set; }

            /// <summary>Set only by --protocol, to exercise the reject path.</summary>
            public int? ProtocolVersion { get; private set; }

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (var i = 1; i < args.Length; i++)
                {
                    var next = i + 1 < args.Length ? args[i + 1] : null;
                    switch (args[i])
                    {
                        case "--host": options.Host = next!; i++; break;
                        case "--bind": options.Bind = next!; i++; break;
                        case "--port": options.Port = int.Parse(next!, CultureInfo.InvariantCulture); i++; break;
                        case "--name": options.Name = next!; i++; break;
                        case "--peers": options.Peers = int.Parse(next!, CultureInfo.InvariantCulture); i++; break;
                        // Deliberately allows an incompatible version so the
                        // reject path can be smoke-tested against a real host.
                        case "--protocol":
                            options.ProtocolVersion = int.Parse(next!, CultureInfo.InvariantCulture);
                            i++;
                            break;
                        case "--auto-ready": options.AutoReady = true; break;
                    }
                }
                return options;
            }
        }
    }
}
