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

        // --- console commands ---

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

        /// <summary>
        /// Replays the last executed turn's captured motion on this machine.
        /// </summary>
        /// <remarks>
        /// The M6 gate. Deliberately round-trips the tracks through the codec
        /// before playing them, so one command exercises the whole pipeline a
        /// client depends on — capture, re-key, turn slicing, encode, decode,
        /// sample, render — with a single game instance. Playing the in-memory
        /// capture directly would prove only that capture works.
        /// <para>
        /// Safe on a host because it writes view transforms only. Authoritative
        /// ECS state is untouched, and the next execution's TransformLinkSystem
        /// pass restores every view regardless. Expect units to slide rather than
        /// walk: poses are out of scope, and sliding is exactly what a client
        /// sees today.
        /// </para>
        /// </remarks>
        public static string ReplayLast()
        {
            if (lastCapture == null || lastCapture.Tracks.Count == 0)
            {
                return "[pb-and-j] no keyframes captured yet — execute a turn first";
            }

            KeyframesMessage decoded;
            try
            {
                var wire = PbjMessageCodec.Encode(new KeyframesMessage(
                    lastCaptureTurn, lastCapture.WindowStart, lastCapture.WindowEnd, lastCapture.Tracks));
                decoded = (KeyframesMessage)PbjMessageCodec.Decode(wire);
            }
            catch (PbjProtocolException e)
            {
                // A capture the codec refuses would have been dropped silently on
                // the wire. Better to learn it here.
                return "[pb-and-j] captured keyframes failed the codec round-trip: " + e.Message;
            }

            var keys = 0;
            foreach (var track in decoded.Tracks)
            {
                keys += track.Transforms.Count;
            }

            // The poses go through the codec too, and for two reasons. The
            // round trip is the same cheap proof the transforms get — a track
            // the encoder would refuse is better learned here than by having
            // the receiving peer drop us as malformed. And it makes this
            // command a genuine one-instance eyeball test of M8: execute a
            // turn, run it, and watch whether the mechs walk. Without it the
            // only way to see a pose is to stand up two games.
            var poses = new List<UnitPoseTrack>();
            try
            {
                foreach (var pose in lastCapture.Poses)
                {
                    if (PoseTracks.TryPrepare(pose, out var prepared) != PoseTrackFault.None)
                    {
                        continue;
                    }
                    var wire = PbjMessageCodec.Encode(new PosesMessage(lastCaptureTurn, 0, 1, prepared));
                    poses.Add(((PosesMessage)PbjMessageCodec.Decode(wire)).Track!);
                }
            }
            catch (PbjProtocolException e)
            {
                return "[pb-and-j] captured poses failed the codec round-trip: " + e.Message;
            }

            // M14's effects take the same trip, and through the SPLIT and the
            // client's own accumulator rather than a single message — that is
            // the half a one-instance test can still prove, and the half that
            // fails invisibly: a part boundary off by one is a turn that
            // reassembles into nothing, which looks exactly like a client where
            // the feature was never built.
            var assets = AssetCapture.None;
            try
            {
                var parts = ReplayAssetParts.Split(Sendable(lastCapture.Assets), out _);
                if (parts.Count > 0)
                {
                    var buffer = new AssetBuffer();
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var wire = PbjMessageCodec.Encode(
                            new ReplayAssetsMessage(lastCaptureTurn, i, parts.Count, parts[i]));
                        buffer.Accept((ReplayAssetsMessage)PbjMessageCodec.Decode(wire));
                    }
                    assets = buffer.Take(lastCaptureTurn);
                }
            }
            catch (PbjProtocolException e)
            {
                return "[pb-and-j] captured effects failed the codec round-trip: " + e.Message;
            }

            KeyframePlayer.Play(decoded.Turn, new KeyframeCapture(
                decoded.WindowStart, decoded.WindowEnd, decoded.Tracks, poses, assets));
            if (!KeyframePlayer.IsPlaying)
            {
                return "[pb-and-j] replay: no recorded unit is present in this combat";
            }

            var line = NetLog.KeyframesReceived(
                decoded.Turn, decoded.Tracks.Count, keys, decoded.WindowStart, decoded.WindowEnd);
            Debug.Log(line);
            Debug.Log(KeyframePlayer.PosedUnits > 0
                ? NetLog.PosesReceived(decoded.Turn, KeyframePlayer.PosedUnits)
                : NetLog.PosesIncomplete(decoded.Turn, poses.Count, lastCapture.Poses.Count));

            var effects = assets.Standalone.Count + assets.Projectiles.Count + assets.Beams.Count;
            Debug.Log(effects > 0
                ? NetLog.AssetsReceived(decoded.Turn, effects)
                : NetLog.AssetsNoneSent(decoded.Turn));
            return line;
        }

        /// <summary>
        /// The captured effects that could travel, checked the way the host
        /// would check them.
        /// </summary>
        /// <remarks>
        /// Applied here so <c>pbj.replay-last</c> shows what a client would
        /// actually receive rather than what the recorder happened to hold. The
        /// per-track drop is the point: a projectile stranded below two keys is
        /// dropped by the host too, and a replay that showed it anyway would be
        /// a more forgiving test than the wire.
        /// </remarks>
        private static AssetCapture Sendable(AssetCapture captured)
        {
            var standalone = new List<StandaloneAssetTrack>(captured.Standalone.Count);
            for (var i = 0; i < captured.Standalone.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Standalone[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    standalone.Add(prepared!);
                }
            }

            var projectiles = new List<ProjectileAssetTrack>(captured.Projectiles.Count);
            for (var i = 0; i < captured.Projectiles.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Projectiles[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    projectiles.Add(prepared!);
                }
            }

            var beams = new List<BeamAssetTrack>(captured.Beams.Count);
            for (var i = 0; i < captured.Beams.Count; i++)
            {
                if (ReplayAssetParts.TryPrepare(captured.Beams[i], out var prepared)
                    == AssetTrackFault.None)
                {
                    beams.Add(prepared!);
                }
            }

            return new AssetCapture(standalone, projectiles, beams);
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

        // --- the save catalogue (M11b) ---
        //
        // M11a shipped no game console commands at all — it is Core-only, driven
        // from the peer REPL — so these are the first way to work the lobby from
        // inside a running game, and they are how M11b is verifiable before M11c's
        // screen exists.
        //
        // Quantum Console splits arguments on spaces, so a save name with spaces
        // has to be quoted: pbj.save-convert "TWICE SHY" fromsp

        public static string Saves()
        {
            var catalogue = LobbyCatalogue.Multiplayer(SaveCatalogueGlue.List());
            if (catalogue.Count == 0)
            {
                return "[pb-and-j] no multiplayer saves yet — pbj.save-as or pbj.save-convert makes one";
            }

            var text = "[pb-and-j] " + catalogue.Count + " multiplayer save(s), newest first:";
            for (var i = 0; i < catalogue.Count; i++)
            {
                text += "\n  " + catalogue[i].Key;
            }
            return text;
        }

        public static string SaveAs(string name)
        {
            var key = SaveCatalogueGlue.SaveAs(name);
            return key == null
                ? "[pb-and-j] could not save as '" + name + "' — see the log for why"
                : "[pb-and-j] saved the current campaign as " + key;
        }

        public static string SaveConvert(string sourceKey, string name)
        {
            var key = SaveCatalogueGlue.Convert(sourceKey, name);
            return key == null
                ? "[pb-and-j] could not convert '" + sourceKey + "' — see the log for why"
                : "[pb-and-j] copied '" + sourceKey + "' to " + key + " — the original is untouched";
        }

        public static string LobbySelect(string key)
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            if (!(runtime.Session is HostSession))
            {
                return "[pb-and-j] only the host chooses the lobby's save";
            }

            // The session accepts any key by design — it reads no disk, the same
            // way it reads no clock — so the guard against selecting something that
            // is not there belongs here, at the edge that can actually look.
            if (!LobbyCatalogue.Contains(SaveCatalogueGlue.List(), key))
            {
                return "[pb-and-j] '" + key + "' is not a multiplayer save — pbj.saves lists them";
            }

            runtime.Post(new LocalLobbySelectEvent(key, SaveCatalogueGlue.Digest(key)));
            return "[pb-and-j] lobby save set to " + key;
        }

        // --- the campaign bit (M11d) ---

        /// <summary>
        /// Whether the loaded campaign is a multiplayer one, and which save it is.
        /// </summary>
        /// <remarks>
        /// The only way to see <see cref="MultiplayerCampaign"/> from inside a
        /// running game. It decides where every subsequent save is written, and a
        /// bit that stuck on would prefix a singleplayer campaign's saves — hiding
        /// them from the load screen and from Continue, which reads as the campaign
        /// having been deleted. Worth being able to look at.
        /// </remarks>
        public static string Campaign()
        {
            return MultiplayerCampaign.Active
                ? "[pb-and-j] multiplayer campaign '" + MultiplayerCampaign.SaveKey
                    + "' — saves stay in the " + LobbySaveNames.Prefix + " namespace"
                : "[pb-and-j] not in a multiplayer campaign — saves are written as the game names them";
        }

        // --- hooks used by the execution patches ---

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
        /// What the lobby screen should draw, or null when there is no session.
        /// </summary>
        /// <remarks>
        /// Composed here rather than in the screen because this is the only place
        /// that holds the runtime, and because host and client are different types
        /// answering the same questions — resolving that once keeps the branch out
        /// of the NGUI code, where no test can reach it. Everything downstream of
        /// this is <see cref="LobbyView"/>, under the gate.
        /// </remarks>
        internal static LobbyView? LobbyView()
        {
            if (runtime == null || killed)
            {
                return null;
            }

            if (runtime.Session is HostSession host)
            {
                return new LobbyView(
                    true, PbjPeerRegistry.HostPeerId, host.Selection.SaveKey, host.LobbyRoster, false);
            }
            if (runtime.Session is ClientSession client)
            {
                return new LobbyView(
                    false, client.PeerId, client.LobbySaveKey, client.LobbyRoster, client.LobbyReadySent);
            }
            return null;
        }

        /// <summary>Reports a finished load back into the session. M11d.</summary>
        internal static void PostLoadFinished(int selectionVersion, LoadOutcome outcome) =>
            runtime?.Post(new LoadFinishedEvent(selectionVersion, outcome));

        /// <summary>The fight is written and can be offered. Host only. M12b.</summary>
        internal static void PostLocalCombatReady(string? saveName, string? digest) =>
            runtime?.Post(new LocalCombatReadyEvent(saveName, digest));

        /// <summary>Reports how joining the host's fight went. Client only. M12b.</summary>
        internal static void PostCombatLoadFinished(LoadOutcome outcome) =>
            runtime?.Post(new CombatLoadFinishedEvent(outcome));

        /// <summary>Tells the session where our base is, for M12a's mirror.</summary>
        internal static void PostLocalBasePosition(float x, float z) =>
            runtime?.Post(new LocalBasePositionEvent(x, z));

        internal static void PostLocalLobbyReady() => runtime?.Post(new LocalLobbyReadyEvent());

        internal static void PostLocalLobbyUnready() => runtime?.Post(new LocalLobbyUnreadyEvent());

        /// <summary>
        /// Chooses the lobby's save, hashing it on the way. Host only.
        /// </summary>
        internal static bool PostLocalLobbySelect(string key)
        {
            // The same guard pbj.lobby-select applies, and for the same reason:
            // the session accepts any key by design because it reads no disk, so
            // every edge that CAN look has to. Without it, a picker showing a
            // stale grid could hand a singleplayer key to the lobby as its
            // campaign, and every peer would ready onto a save they cannot have.
            if (!LobbyCatalogue.Contains(SaveCatalogueGlue.List(), key))
            {
                Debug.LogWarning("[pb-and-j] refusing to select '" + key + "' — not a multiplayer save");
                return false;
            }

            runtime?.Post(new LocalLobbySelectEvent(key, SaveCatalogueGlue.Digest(key)));
            return true;
        }

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
            //
            // Keyframes are read in the same call for the same reason, and it is
            // load-bearing here: the final key capture appends comes from the
            // same read the snapshot does, which is what makes "playback ends
            // where the correction put it" true rather than hoped for.
            var snapshot = bridge.CaptureSnapshot();
            var keyframes = bridge.CaptureKeyframes();
            lastCapture = keyframes;
            lastCaptureTurn = bridge.CurrentTurn;
            runtime.Post(new LocalTurnCompleteEvent(
                bridge.ComputeStateDigest(), snapshot, keyframes));
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

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(Host), new Type[0], "pbj.host");
            Add(nameof(Host), new[] { typeof(int) }, "pbj.host");
            Add(nameof(Host), new[] { typeof(string), typeof(int), typeof(string) }, "pbj.host");
            Add(nameof(Join), new[] { typeof(string) }, "pbj.join");
            Add(nameof(Join), new[] { typeof(string), typeof(int) }, "pbj.join");
            Add(nameof(Join), new[] { typeof(string), typeof(int), typeof(string) }, "pbj.join");
            Add(nameof(NetStatus), new Type[0], "pbj.net-status");
            Add(nameof(NetStop), new Type[0], "pbj.net-stop");
            Add(nameof(Ready), new Type[0], "pbj.ready");
            Add(nameof(Unready), new Type[0], "pbj.unready");
            Add(nameof(Rejoin), new Type[0], "pbj.rejoin");
            Add(nameof(ReplayLast), new Type[0], "pbj.replay-last");
            Add(nameof(ScenarioPull), new Type[0], "pbj.scenario-pull");
            Add(nameof(Saves), new Type[0], "pbj.saves");
            Add(nameof(SaveAs), new[] { typeof(string) }, "pbj.save-as");
            Add(nameof(SaveConvert), new[] { typeof(string), typeof(string) }, "pbj.save-convert");
            Add(nameof(LobbySelect), new[] { typeof(string) }, "pbj.lobby-select");
            Add(nameof(Campaign), new Type[0], "pbj.campaign");
            AddFrom(typeof(ConnectScreenGlue), nameof(ConnectScreenGlue.Connect),
                new Type[0], "pbj.connect");
            AddFrom(typeof(ConnectScreenGlue), nameof(ConnectScreenGlue.ConnectForget),
                new Type[0], "pbj.connect-forget");
            AddFrom(typeof(LobbyScreenGlue), nameof(LobbyScreenGlue.Lobby),
                new Type[0], "pbj.lobby");
            AddFrom(typeof(CombatShipGlue), nameof(CombatShipGlue.ShipFight),
                new Type[0], "pbj.ship-fight");
        }

        private static void AddFrom(Type owner, string methodName, Type[] parameters, string command)
        {
            var method = owner.GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
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

            // Immediately after the pump, and that ordering is the point: the
            // pump runs its effects synchronously, so a ShipCombatEffect raised
            // by this frame's combat edge has already armed the glue by now. The
            // poll can never run ahead of the session that asked for it.
            CombatShipGlue.Tick();

            // Outside the pump's session guard on purpose: pbj.replay-last has to
            // work on a host with no session, and playback must keep running for
            // its full window even if the session faults halfway through.
            // Unscaled, because the game parks Time.timeScale at zero during
            // planning.
            KeyframePlayer.Advance(Time.unscaledDeltaTime);

            // THROWAWAY (M8 recon) — samples Time.timeScale across exactly the
            // playback window, which is the one M8 question a running game has
            // never answered. Goes with ReplayProbeGlue.
            ReplayProbeGlue.SampleDuringPlayback();

            // Also outside the session guard: the connect screen exists in order
            // to start a session, so it must run when there is none.
            ConnectScreenGlue.Tick();
            LobbyScreenGlue.Tick();

            // Step 0 of the drive rig, and it runs from here rather than from a
            // console command on purpose: the question is whether Quantum
            // Console works when nobody has opened it, and asking through a
            // console command would answer a different one. Self-disarming after
            // a single run.
#if PBJ_DRIVE
            DriveProbeGlue.Tick();

            // One queued drive command per frame. Last, so a driven command
            // observes the state this frame's pump and glue have already
            // settled, rather than a half-updated one.
            DriveGlue.Tick();
#endif
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "OnApplicationQuit")]
    internal static class Patch_Heartbeat_OnApplicationQuit
    {
        private static void Postfix()
        {
            NetGlue.Shutdown();
#if PBJ_DRIVE
            DriveGlue.Stop();
#endif
        }
    }
}
