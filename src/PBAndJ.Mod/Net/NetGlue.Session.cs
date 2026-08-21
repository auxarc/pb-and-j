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
    // Console commands that act on a session already running: what it is doing,
    // stopping it, the local player's readiness, and asking the host for its
    // combat save.
    internal static partial class NetGlue
    {
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

        /// <summary>
        /// Asks the host for its combat save — M9's replacement for carrying the
        /// folder across by hand.
        /// </summary>
        /// <remarks>
        /// Not usually needed: a client in the lobby is offered the save on
        /// handshake and asks for it automatically unless it already holds it.
        /// This is the override for the cases that deliberately excludes — a save
        /// deleted since, a host that re-saved mid-session, or simply wanting the
        /// transfer now.
        /// <para>
        /// The save is written but never loaded. Entering it is
        /// <c>pbj.combat-load</c>, by hand, because loading a save on a network
        /// message would yank the player out of whatever they were doing.
        /// </para>
        /// </remarks>
        public static string ScenarioPull()
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            if (!(runtime.Session is ClientSession))
            {
                return "[pb-and-j] only a client pulls a scenario — the host is the one that has it";
            }
            runtime.Post(new LocalScenarioPullEvent());
            return "[pb-and-j] asked the host for its combat save";
        }
    }
}
