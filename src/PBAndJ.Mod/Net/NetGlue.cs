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

        // The runtime's own bridge, kept rather than rebuilt per call. Building a
        // throwaway was harmless while every field on it was static; it stopped
        // being harmless once one call has to produce a snapshot and a digest
        // that describe the same instant.
        private static CombatGameBridge? bridge;

        private static TcpHostTransport? hostTransport;
        private static TcpClientTransport? clientTransport;
        private static int mainThreadId = -1;
        private static bool killed;

        // Deliberately survives Shutdown: a reconnect has nothing to present if
        // the credential dies with the session that issued it.
        private static string? resumeToken;
        private static string? resumeSessionId;
        private static int resumePeerId = -1;
        private static string? lastAddress;
        private static int lastPort;

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
                bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpHostTransport(mailbox, IPAddress.Loopback, port);
                transport.Start();
                hostTransport = transport;

                var session = new HostSession(HostName(), NewSessionId(), MaxPeers, bridge, NewSessionSecret());
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

        public static string Join(string address, int port) => Connect(address, port, resuming: false);

        /// <summary>
        /// Reconnects to the last host, reclaiming the units we held.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Join"/> because a client session is faulted
        /// terminally by a disconnect — coming back means a fresh transport and a
        /// fresh session, carrying the token the old one was issued.
        /// </remarks>
        public static string Rejoin()
        {
            if (resumeToken == null || lastAddress == null)
            {
                return "[pb-and-j] nothing to rejoin — no previous session on this launch";
            }
            return Connect(lastAddress, lastPort, resuming: true);
        }

        private static string Connect(string address, int port, bool resuming)
        {
            if (runtime != null)
            {
                return "[pb-and-j] a session is already running — pbj.net-stop first";
            }
            try
            {
                bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpClientTransport(mailbox);
                clientTransport = transport;

                var session = resuming
                    ? new ClientSession(HostName(), ModVersion(), bridge, resumeSessionId, resumePeerId, resumeToken)
                    : new ClientSession(HostName(), ModVersion(), bridge);
                runtime = new PbjRuntime(transport, bridge, new UnityLog(), mailbox, session);
                killed = false;
                lastAddress = address;
                lastPort = port;

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

        /// <summary>
        /// Withdraws a submitted turn so it can be re-planned.
        /// </summary>
        /// <remarks>
        /// A console command rather than a UI hook: the game has no un-ready
        /// button to intercept, because single-player has nothing to wait for.
        /// </remarks>
        public static string Unready()
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            runtime.Post(new LocalUnreadyEvent());
            return "[pb-and-j] local un-ready posted";
        }

        // --- hooks used by the execution patches ---

        internal static bool HasSession => runtime != null && !killed;

        internal static void PostLocalReady()
        {
            runtime?.Post(new LocalReadyEvent());
        }

        internal static void PostLocalTurnComplete()
        {
            if (runtime == null || bridge == null)
            {
                return;
            }
            // One capture, then the digest projected from it — so the digest
            // describes exactly the state that goes on the wire. Reading the
            // bridge twice, or building a throwaway one as this used to, would
            // let the two drift apart between calls.
            var snapshot = bridge.CaptureSnapshot();
            runtime.Post(new LocalTurnCompleteEvent(bridge.ComputeStateDigest(), snapshot));
        }

        /// <summary>
        /// A turn advanced without going through the barrier — scenario content
        /// calling CombatForceExecution, or the debug console. The host treats
        /// it as authoritative rather than fighting it.
        /// </summary>
        internal static void NotifyExternalTurnAdvance(int from, int to)
        {
            if (runtime?.Session is HostSession)
            {
                Debug.Log($"[pb-and-j] turn advanced outside the barrier ({from} -> {to}) — scenario or console");
            }
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
                // The double overload, not the float one: float seconds lose
                // sub-millisecond resolution after a few hours of process
                // uptime, and since M5c this value drives the timeout logic.
                runtime.Pump(Time.realtimeSinceStartupAsDouble);
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
                // Capture the credential before the session holding it goes.
                if (runtime?.Session is ClientSession leaving && leaving.ResumeToken != null)
                {
                    resumeToken = leaving.ResumeToken;
                    resumeSessionId = leaving.SessionId;
                    resumePeerId = leaving.PeerId;
                }
                runtime = null;
                bridge = null;
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

        /// <summary>
        /// The per-session secret resume tokens are derived from.
        /// </summary>
        /// <remarks>
        /// Minted here rather than in Core so the session stays a deterministic
        /// pure machine, and never sent — which is the whole point. A token
        /// derived from the session id or peer names would be computable by
        /// anyone who has seen a Welcome.
        /// </remarks>
        private static string NewSessionSecret()
        {
            return Guid.NewGuid().ToString("N");
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
            Add(nameof(Unready), new Type[0], "pbj.unready");
            Add(nameof(Rejoin), new Type[0], "pbj.rejoin");
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
