using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod
{
    // M4 Step 0a: does Unity's Mono System.Net.Sockets actually work under
    // Proton/Wine? This is a throwaway probe, deliberately free of any
    // PBAndJ.Core types — it exists to answer one question before the wire
    // layer is written, and a failure here re-plans the whole transport.
    //
    // Everything runs on a background thread and the console command returns
    // immediately, so a socket that never returns cannot wedge the game loop.
    // Debug.Log is called off the main thread here, which Unity supports; the
    // real transport does not do this (log ordering is kept deterministic by
    // routing transport log lines through the main-thread pump instead).
    [ExcludeFromCodeCoverage]
    internal static class SocketProbeGlue
    {
        private const int PayloadSize = 16;
        private const int DefaultExternalPort = 27600;
        private const int ExternalWaitSeconds = 60;

        public static string NetSelfTest()
        {
            return NetSelfTest(DefaultExternalPort);
        }

        public static string NetSelfTest(int externalPort)
        {
            var thread = new Thread(() => Run(externalPort)) { IsBackground = true, Name = "pbj-socket-probe" };
            thread.Start();
            return "[pb-and-j] socket selftest started — watch Player.log";
        }

        private static void Run(int externalPort)
        {
            try
            {
                RunLoopbackEcho();
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] socket selftest: loopback echo FAILED — " + e.GetType().Name + ": " + e.Message);
                return;
            }

            try
            {
                RunExternalAccept(externalPort);
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] socket selftest: external accept FAILED — " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 1 — in-process listener + client, Wine to Wine.
        private static void RunLoopbackEcho()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Debug.Log("[pb-and-j] socket selftest: listener bound 127.0.0.1:" + port);

            var echo = new Thread(() => EchoOnce(listener)) { IsBackground = true, Name = "pbj-socket-probe-echo" };
            echo.Start();

            var sent = new byte[PayloadSize];
            for (var i = 0; i < PayloadSize; i++)
            {
                sent[i] = (byte)(i * 7 + 1);
            }
            var received = new byte[PayloadSize];

            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendTimeout = 1000;
                client.ReceiveTimeout = 5000;
                client.Connect(IPAddress.Loopback, port);
                var stream = client.GetStream();
                stream.Write(sent, 0, sent.Length);
                ReadExactly(stream, received, received.Length);
            }

            echo.Join(2000);
            listener.Stop();

            var match = true;
            for (var i = 0; i < PayloadSize; i++)
            {
                if (sent[i] != received[i])
                {
                    match = false;
                    break;
                }
            }

            Debug.Log("[pb-and-j] socket selftest: listener bound 127.0.0.1:" + port
                + " | sent " + PayloadSize + " | received " + PayloadSize
                + " | " + (match ? "MATCH" : "MISMATCH"));
        }

        private static void EchoOnce(TcpListener listener)
        {
            try
            {
                using (var server = listener.AcceptTcpClient())
                {
                    server.NoDelay = true;
                    server.SendTimeout = 1000;
                    server.ReceiveTimeout = 5000;
                    var stream = server.GetStream();
                    var buffer = new byte[PayloadSize];
                    ReadExactly(stream, buffer, buffer.Length);
                    stream.Write(buffer, 0, buffer.Length);
                }
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] socket selftest: echo thread FAILED — " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 — the boundary M4 actually ships: a native-Linux process
        // (pbj-peer, or `nc 127.0.0.1 <port>` for this probe) connecting into
        // the Wine-hosted game. Phase 1 never crosses it.
        private static void RunExternalAccept(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Debug.Log("[pb-and-j] socket selftest: awaiting external connection on 127.0.0.1:" + port
                + " for " + ExternalWaitSeconds + "s — run `nc 127.0.0.1 " + port + "` from a host terminal");

            var deadline = DateTime.UtcNow.AddSeconds(ExternalWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(100);
                    continue;
                }

                using (var peer = listener.AcceptTcpClient())
                {
                    var remote = peer.Client.RemoteEndPoint;
                    peer.NoDelay = true;
                    peer.SendTimeout = 1000;
                    peer.ReceiveTimeout = 5000;
                    var stream = peer.GetStream();
                    var greeting = System.Text.Encoding.UTF8.GetBytes("pbj-probe-ok\n");
                    stream.Write(greeting, 0, greeting.Length);

                    var buffer = new byte[256];
                    var read = 0;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException)
                    {
                        // peer closed without sending — still a successful accept
                    }

                    Debug.Log("[pb-and-j] socket selftest: external peer connected from " + remote
                        + " | greeted 13 | read " + read + " | EXTERNAL OK");
                }

                listener.Stop();
                return;
            }

            listener.Stop();
            Debug.Log("[pb-and-j] socket selftest: no external connection within "
                + ExternalWaitSeconds + "s — EXTERNAL UNVERIFIED");
        }

        private static void ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new IOException("stream closed after " + offset + " of " + count + " bytes");
                }
                offset += read;
            }
        }

        internal static void RegisterConsoleCommands()
        {
            var noArg = typeof(SocketProbeGlue).GetMethod(
                nameof(NetSelfTest), BindingFlags.Static | BindingFlags.Public, null, new Type[0], null);
            var withPort = typeof(SocketProbeGlue).GetMethod(
                nameof(NetSelfTest), BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(noArg, "pbj.net-selftest"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(withPort, "pbj.net-selftest"));
        }
    }
}
