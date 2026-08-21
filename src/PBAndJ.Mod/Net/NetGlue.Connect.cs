using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Content.Code.Utility;
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
    // Starting a session and joining one: pbj.host, pbj.join and pbj.rejoin.
    //
    // Join and Rejoin funnel through the private Connect. Host does NOT -- it
    // opens a listening transport instead, and shares only the helpers below.
    // Those are here because Host and Connect are the only callers they have;
    // LastRejection, because the connect screen asks it why an attempt failed.
    internal static partial class NetGlue
    {
        public static string Host() => Host(DefaultPort);

        public static string Host(int port) => Host("127.0.0.1", port, null);

        /// <summary>
        /// Hosts on a specific interface, for play with someone who is not on
        /// this machine.
        /// </summary>
        /// <remarks>
        /// A separate form rather than a changed default, because the promise in
        /// the README and the mod policy is that networking is strictly opt-in.
        /// Reaching the outside world should be something a player typed, not
        /// something that happened.
        /// <para>
        /// A non-loopback bind <em>requires</em> a passphrase. This protocol is
        /// open source, so a listener on a routable address with no passphrase is
        /// joinable by anything that finds the port — and an accepted peer can
        /// submit orders for the units it is dealt. The passphrase travels in the
        /// clear over plain TCP: it keeps strangers out, it is not confidentiality
        /// against anyone sitting on the path.
        /// </para>
        /// </remarks>
        public static string Host(string bind, int port, string? passphrase)
        {
            if (runtime != null)
            {
                return "[pb-and-j] a session is already running — pbj.net-stop first";
            }

            // The rules themselves live in Core, under the coverage gate, so the
            // connect screen and this command cannot come to disagree about what
            // is allowed to listen. Only the wording is composed here.
            var problem = ConnectRules.CheckHostBind(bind, passphrase);
            if (problem == ConnectProblem.BindNotAnIpAddress)
            {
                return "[pb-and-j] '" + bind + "' is not an IP address — try 127.0.0.1 or 0.0.0.0";
            }
            if (problem == ConnectProblem.OpenBindNeedsPassphrase)
            {
                return "[pb-and-j] refusing to listen on " + bind + " without a passphrase — "
                    + "use: pbj.host " + bind + " " + port + " <passphrase>";
            }

            // Cannot fail: CheckHostBind just parsed it.
            IPAddress.TryParse(bind.Trim(), out var address);
            var loopback = IPAddress.IsLoopback(address);

            try
            {
                // Starting a session is the explicit opt-in to networking, and
                // also the moment a stale build is about to matter — a mismatched
                // mod version is refused by the handshake and reads as a netcode
                // bug otherwise. Fire and forget; nothing here waits on it.
                UpdateGlue.CheckInBackground();

                bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpHostTransport(mailbox, address, port);
                transport.Start();
                hostTransport = transport;

                var session = new HostSession(
                    HostName(), NewSessionId(), MaxPeers, bridge, NewSessionSecret(),
                    new SessionRequirements(ModVersion(), GameBuild(), passphrase));
                runtime = new PbjRuntime(transport, bridge, new UnityLog(), mailbox, session);
                killed = false;

                var line = NetLog.HostListening(bind, transport.Port, PbjProtocol.Version, MaxPeers);
                Debug.Log(line);
                if (!loopback)
                {
                    // Loud, and once, so nobody discovers after the fact that
                    // their game was reachable from off the machine.
                    Debug.LogWarning(NetLog.HostListeningOpenly(bind, transport.Port));
                }
                return line;
            }
            catch (Exception e)
            {
                Shutdown();
                return "[pb-and-j] failed to host: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// This installation's Phantom Brigade build, as the game reports it.
        /// </summary>
        /// <remarks>
        /// The whole string, not a parsed version: two peers only need to agree,
        /// and the raw value distinguishes builds that share a version number.
        /// Null if the game cannot say, which the handshake reads as "cannot
        /// say" rather than "does not match".
        /// </remarks>
        private static string? GameBuild()
        {
            try
            {
                return BuildInfoHelper.GetBuildInfo();
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] could not read the game build: " + e.GetType().Name);
                return null;
            }
        }

        public static string Join(string address) => Join(address, DefaultPort);

        public static string Join(string address, int port) => Join(address, port, null);

        public static string Join(string address, int port, string? passphrase)
        {
            sessionPassphrase = passphrase;
            return Connect(address, port, resuming: false);
        }

        /// <summary>
        /// Reconnects to the last host, reclaiming the units we held.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Join(string, int, string)"/> because a client session
        /// is faulted
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
                // Starting a session is the explicit opt-in to networking, and
                // also the moment a stale build is about to matter — a mismatched
                // mod version is refused by the handshake and reads as a netcode
                // bug otherwise. Fire and forget; nothing here waits on it.
                UpdateGlue.CheckInBackground();

                bridge = new CombatGameBridge();
                var mailbox = new PbjMailbox(MailboxCapacity);
                var transport = new TcpClientTransport(mailbox);
                clientTransport = transport;

                var session = resuming
                    ? new ClientSession(HostName(), ModVersion(), bridge, resumeSessionId, resumePeerId, resumeToken)
                    : new ClientSession(HostName(), ModVersion(), bridge);

                // Set before Start(), which is what composes the Hello or Rejoin.
                session.GameBuild = GameBuild();
                session.Passphrase = sessionPassphrase;

                runtime = new PbjRuntime(transport, bridge, new UnityLog(), mailbox, session);
                killed = false;
                lastAddress = address;
                lastPort = port;

                transport.Connect(address, port);

                // Remembered so the title-menu entry can offer it back. Recorded
                // on the attempt rather than on a successful handshake because
                // the details are just as worth keeping when the host was not
                // up yet — which is the common case when two people are still
                // getting set up.
                ConnectScreenGlue.Remember(address, port, sessionPassphrase);

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

        /// <summary>
        /// Why the host refused us, if it did and we are a client.
        /// </summary>
        /// <remarks>
        /// Retained by ClientSession rather than composed here, so the connect
        /// screen can name the actual problem instead of saying "failed" — which
        /// sends people to check their firewall when the passphrase has a typo.
        /// </remarks>
        internal static RejectReason? LastRejection()
        {
            return runtime?.Session is ClientSession client ? client.Rejection : null;
        }

        private static string HostName()
        {
            var name = SystemInfo.deviceName;
            return string.IsNullOrWhiteSpace(name) ? "player" : name;
        }

        // One source of truth, in Core, shared with the standalone harness —
        // see PbjProtocol.ModVersion for why it stopped being a literal here.
        private static string ModVersion() => PbjProtocol.ModVersion;

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
    }
}
