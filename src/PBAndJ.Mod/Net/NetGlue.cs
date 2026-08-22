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
    // The runtime instance, the state that outlives a session, and the frame
    // pump. Start here.
    //
    // Shutdown is here rather than with a caller because callers in several
    // parts share it and none of them owns it.

    // Humble-object glue: owns the runtime instance, the console commands and
    // the frame pump. All logic lives in PBAndJ.Core behind the 100% gate.
    [ExcludeFromCodeCoverage]
    internal static partial class NetGlue
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

        // Survives Shutdown alongside the resume token: pbj.rejoin has to present
        // the same passphrase the original join did, and the session that knew it
        // is gone by then.
        private static string? sessionPassphrase;

        // The last turn's captured motion, kept for pbj.replay-last. Survives
        // Shutdown for the same reason the resume token does: the command is a
        // diagnostic and has to work after a session ends.
        private static KeyframeCapture? lastCapture;
        private static int lastCaptureTurn = -1;

        internal sealed class UnityLog : IPbjLog
        {
            public void Log(string line) => Debug.Log(line);
        }
        internal static bool HasSession => runtime != null && !killed;

        /// <summary>
        /// Whether this machine is hosting. Meaningless without
        /// <see cref="HasSession"/>, and false when there is no session at all.
        /// </summary>
        /// <remarks>
        /// Asked by <see cref="PassengerGlue"/> to decide who may drive the
        /// overworld. Reads the session's own type rather than remembering what
        /// was clicked: the connect screen can start either kind, the console
        /// can start either kind, and a remembered flag would be a second source
        /// of truth for something the runtime already knows.
        /// </remarks>
        internal static bool IsHost => HasSession && runtime!.Session is HostSession;

        /// <summary>
        /// The live session, or null when there is none. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Narrower on purpose than exposing <c>runtime</c> itself: the one thing
        /// asking for this is <see cref="WreckingPatches"/>, which needs to know
        /// whether a <c>ClientSession</c> is live and what state it is in, and a
        /// glue-wide handle on the runtime would be a second way to reach the
        /// pump and the transports.
        /// <para>
        /// Reads the runtime rather than a remembered flag, for the same reason
        /// <see cref="IsHost"/> does: the connect screen and the console can each
        /// start either kind of session, and a remembered answer would be a
        /// second source of truth for something the runtime already knows.
        /// </para>
        /// </remarks>
        internal static IPbjSession? Session => HasSession ? runtime!.Session : null;


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
    }
}
