using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PBAndJ.Core.Net;
using PBAndJ.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Humble-object glue: owns the runtime instance, the console commands and
    // the frame pump. All logic lives in PBAndJ.Core behind the 100% gate.
    [ExcludeFromCodeCoverage]
    internal static class NetGlue
    {
        private const int DefaultPort = 27600;
        private const int MaxPeers = 3;
        private const int MailboxCapacity = 4096;

        private static PbjRuntime? runtime;
        private static TcpHostTransport? hostTransport;
        private static TcpClientTransport? clientTransport;
        private static int mainThreadId = -1;
        private static bool killed;

        internal sealed class UnityLog : IPbjLog
        {
            public void Log(string line) => Debug.Log(line);
        }

        // --- console commands ---

        public static string Host() => Host(DefaultPort);

        public static string Host(int port)
        {
            if (runtime != null)
            {
                return "[pb-and-j] a session is already running — pbj.net-stop first";
            }
            try
            {
                var bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpHostTransport(mailbox, IPAddress.Loopback, port);
                transport.Start();
                hostTransport = transport;

                var session = new HostSession(HostName(), NewSessionId(), MaxPeers, bridge);
                runtime = new PbjRuntime(transport, bridge, new UnityLog(), mailbox, session);
                killed = false;

                var line = NetLog.HostListening("127.0.0.1", transport.Port, PbjProtocol.Version, MaxPeers);
                Debug.Log(line);
                return line;
            }
            catch (Exception e)
            {
                Shutdown();
                return "[pb-and-j] failed to host: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public static string Join(string address) => Join(address, DefaultPort);

        public static string Join(string address, int port)
        {
            if (runtime != null)
            {
                return "[pb-and-j] a session is already running — pbj.net-stop first";
            }
            try
            {
                var bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpClientTransport(mailbox);
                clientTransport = transport;

                var session = new ClientSession(HostName(), ModVersion(), bridge);
                runtime = new PbjRuntime(transport, bridge, new UnityLog(), mailbox, session);
                killed = false;

                transport.Connect(address, port);
                var line = NetLog.ClientConnecting(address, port, HostName());
                Debug.Log(line);
                return line;
            }
            catch (Exception e)
            {
                Shutdown();
                return "[pb-and-j] failed to join: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public static string NetStatus()
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            if (runtime.Session is HostSession host)
            {
                return NetLog.Status("HOST", host.State.ToString(), host.Turn, host.ParticipantCount, host.ReadyCount);
            }
            var client = (ClientSession)runtime.Session;
            return NetLog.Status("CLIENT", client.State.ToString(), client.Turn, 1, 0);
        }

        public static string NetStop()
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            var peers = runtime.Session is HostSession host ? host.Peers.Count : 0;
            Shutdown();
            var line = NetLog.SessionClosed(peers);
            Debug.Log(line);
            return line;
        }

        /// <summary>Marks the local player ready — the console stand-in for Execute.</summary>
        public static string Ready()
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            runtime.Post(new LocalReadyEvent());
            return "[pb-and-j] local ready posted";
        }

        // --- the pump ---

        internal static void Pump()
        {
            if (runtime == null || killed)
            {
                return;
            }

            if (mainThreadId == -1)
            {
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }
            else if (mainThreadId != Thread.CurrentThread.ManagedThreadId)
            {
                // Entitas has no locking anywhere; a component write from another
                // thread corrupts group membership and crashes somewhere else
                // entirely. Fail loudly instead.
                killed = true;
                Debug.LogError("[pb-and-j] pump entered from a non-main thread — networking stopped");
                return;
            }

            try
            {
                runtime.Pump(Time.realtimeSinceStartup);
            }
            catch (Exception e)
            {
                // Fire once: a per-frame exception would flood the log and tank
                // the frame rate.
                killed = true;
                Debug.LogError(NetLog.PumpFailed(e.GetType().Name + ": " + e.Message));
                Shutdown();
            }
        }

        internal static void Shutdown()
        {
            try
            {
                runtime?.Stop();
                hostTransport?.Stop();
                clientTransport?.Stop();
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] teardown error: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                runtime = null;
                hostTransport = null;
                clientTransport = null;
                CombatGameBridge.ResetLock();
            }
        }

        private static string HostName()
        {
            var name = SystemInfo.deviceName;
            return string.IsNullOrWhiteSpace(name) ? "player" : name;
        }

        private static string ModVersion() => "0.2.0";

        private static string NewSessionId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(Host), new Type[0], "pbj.host");
            Add(nameof(Host), new[] { typeof(int) }, "pbj.host");
            Add(nameof(Join), new[] { typeof(string) }, "pbj.join");
            Add(nameof(Join), new[] { typeof(string), typeof(int) }, "pbj.join");
            Add(nameof(NetStatus), new Type[0], "pbj.net-status");
            Add(nameof(NetStop), new Type[0], "pbj.net-stop");
            Add(nameof(Ready), new Type[0], "pbj.ready");
        }

        private static void Add(string methodName, Type[] parameters, string command)
        {
            var method = typeof(NetGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }

    // The pump site: Heartbeat.Update is the main thread in every game state,
    // including the main menu where the lobby must work and no Entitas combat
    // system exists. It is also where SteamHelper.RunCallbacks already lives, so
    // a future Steam transport pumps from the identical place.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "Update")]
    internal static class Patch_Heartbeat_Update
    {
        private static void Postfix()
        {
            NetGlue.Pump();
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "OnApplicationQuit")]
    internal static class Patch_Heartbeat_OnApplicationQuit
    {
        private static void Postfix()
        {
            NetGlue.Shutdown();
        }
    }
}
