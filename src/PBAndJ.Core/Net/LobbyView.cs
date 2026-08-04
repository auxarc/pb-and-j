using System.Collections.Generic;
using System.Globalization;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Every word the lobby screen shows.
    /// </summary>
    /// <remarks>
    /// The same second register <see cref="ConnectText"/> established, and for the
    /// same reason: <see cref="NetLog"/>'s lines carry a <c>[pb-and-j]</c> prefix
    /// and render enum names verbatim, which is right for a shared Player.log and
    /// noise on a label.
    /// <para>
    /// Exact-string tested. Changing a line means changing its test, which is the
    /// point — this wording is the feature's whole surface.
    /// </para>
    /// </remarks>
    public static class LobbyText
    {
        /// <remarks>
        /// Not "Multiplayer" — that is the connect screen, the door. This is the
        /// room behind it, and giving both the same name would make Back
        /// ambiguous.
        /// </remarks>
        public static string Title() => "Lobby";

        public static string ChooseSaveLabel() => "Choose save";

        /// <remarks>
        /// The button's resting wording. Which of Ready / Not ready it actually
        /// shows is <see cref="LobbyView.ReadyLabel"/>'s answer, refreshed every
        /// frame — this is only what it is built with.
        /// </remarks>
        public static string ReadyLabel() => "Ready";

        public static string Opened() => "Lobby open.";

        public static string NotOpened() => "The lobby did not open — see the log.";

        public static string NoSession() =>
            "Host or join a session first — the lobby is what you do once you are connected.";

        public static string CouldNotBuild() =>
            "The lobby screen could not be built. Use pbj.saves and pbj.lobby-select from the console.";

        /// <remarks>
        /// Shown instead of silently ignoring the click. The host's own
        /// <c>HandleLocalLobbyReady</c> refuses a ready with no save chosen and
        /// says nothing about it, so without this the button is dead and unexplained.
        /// </remarks>
        /// <remarks>
        /// The picker should not be able to offer one, but the lobby refuses it
        /// out loud rather than appearing to accept a save it then never uses.
        /// </remarks>
        public static string SaveNotUsable() => "That is not a multiplayer save.";

        public static string NothingToAgreeTo() => "There is no save chosen yet to agree to.";

        /// <remarks>
        /// Appended while we have asked to be ready and the host has not confirmed.
        /// A click that never arrived must not look like one that did.
        /// </remarks>
        public static string ReadyPendingSuffix() => " · waiting for the host to confirm you";

        public static string StartLabel() => "Start";

        /// <remarks>
        /// Shown when the barrier is full and Start is pressed. The button is
        /// present and says why rather than being hidden: a screen that shows
        /// nothing once everyone is ready cannot be told apart from one that has
        /// failed, which is the bug the connect screen was rebuilt around. Saying
        /// nothing at all would be the other failure — shipping something inert
        /// that looks live.
        /// </remarks>
        public static string StartNotImplemented() =>
            "The synchronised load arrives in the next update — everyone is ready, but nothing loads yet.";

        /// <summary>Why a proposed save name was refused, in the screen's voice.</summary>
        public static string DescribeProblem(LobbySaveProblem problem)
        {
            switch (problem)
            {
                case LobbySaveProblem.None:
                    // One element, one job: the warning line is blank when there
                    // is nothing to warn about, rather than reporting success in
                    // the space reserved for failure.
                    return string.Empty;
                case LobbySaveProblem.Empty:
                    return "Type a name for the new save.";
                case LobbySaveProblem.TooLong:
                    return "That name is too long.";
                case LobbySaveProblem.BadCharacter:
                    return "Use only letters, numbers, dots, dashes and underscores.";
                case LobbySaveProblem.AlreadyPrefixed:
                    return "Leave off the pbj_ prefix — it is added for you.";
                case LobbySaveProblem.Reserved:
                    return "That name is reserved by the game. Pick another.";
                case LobbySaveProblem.Duplicate:
                    return "A multiplayer save already has that name.";
                default:
                    // Reachable only from a cast. A screen that renders nothing
                    // for an unrecognised problem is a screen that silently
                    // accepts a name the rules refused.
                    return "That name cannot be used.";
            }
        }
    }

    /// <summary>
    /// What the lobby screen draws, given what the session knows.
    /// </summary>
    /// <remarks>
    /// A humble object, the same split <see cref="ConnectForm"/> is to the connect
    /// screen: the glue hands in plain values and reads back finished lines and
    /// flags, so every rule about who may do what is under the coverage gate
    /// rather than tangled into NGUI code that no test can reach.
    /// <para>
    /// Deliberately built from primitives rather than from a session, because
    /// <c>HostSession</c> and <c>ClientSession</c> are different types that answer
    /// the same questions. One view serves both.
    /// </para>
    /// </remarks>
    public sealed class LobbyView
    {
        /// <summary>
        /// How many roster lines the screen has room for, overflow line included.
        /// </summary>
        /// <remarks>
        /// The screen is a fixed stack of cloned labels: this UI has no scroll
        /// view we know of, and the lobby is small enough not to need one. The
        /// wire roster caps at 16, so overflow is reachable rather than
        /// theoretical.
        /// </remarks>
        public const int MaxRosterLines = 7;

        private static readonly string[] NoLines = new string[0];
        private static readonly LobbyPeerState[] NoPeers = new LobbyPeerState[0];

        private readonly IReadOnlyList<LobbyPeerState> roster;

        public LobbyView(
            bool isHost,
            int localPeerId,
            string? saveKey,
            IReadOnlyList<LobbyPeerState>? roster,
            bool readySent)
        {
            IsHost = isHost;
            LocalPeerId = localPeerId;
            SaveKey = saveKey;
            ReadySent = readySent;
            this.roster = roster ?? NoPeers;
        }

        public bool IsHost { get; }

        public int LocalPeerId { get; }

        public string? SaveKey { get; }

        /// <summary>
        /// Whether we have asked to be ready. <b>Not</b> whether we are — see
        /// <see cref="IsLocallyReady"/>.
        /// </summary>
        public bool ReadySent { get; }

        public bool HasSave => !string.IsNullOrEmpty(SaveKey);

        /// <summary>Only the host chooses the save.</summary>
        /// <remarks>
        /// The authority split: the host drives the overworld, so it owns which
        /// campaign the session plays. A client sees the selection read-only.
        /// </remarks>
        public bool CanChooseSave => IsHost;

        /// <summary>
        /// Whether the local member is ready, <em>as the host reports it</em>.
        /// </summary>
        /// <remarks>
        /// Read from the roster and not from <see cref="ReadySent"/>, which is
        /// only what we asked for. Rendering the request instead of the answer is
        /// how a screen shows a success that never happened — the failure the
        /// connect screen already paid for once, where a working connection and a
        /// silent one looked identical.
        /// </remarks>
        public bool IsLocallyReady
        {
            get
            {
                for (var i = 0; i < roster.Count; i++)
                {
                    if (roster[i].PeerId == LocalPeerId)
                    {
                        return roster[i].Ready;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Whether the ready toggle should do anything.
        /// </summary>
        /// <remarks>
        /// False with no save chosen because the host's own
        /// <c>HandleLocalLobbyReady</c> refuses that case outright. A live button
        /// whose click is silently swallowed is worse than an unavailable one.
        /// </remarks>
        public bool CanReady => HasSave && roster.Count > 0;

        /// <summary>
        /// We have asked to be ready and the host has not said so yet.
        /// </summary>
        /// <remarks>
        /// The screen must be able to tell "asked, waiting" from "not asked", or a
        /// click that never reached the host looks exactly like one that did —
        /// the silent-success failure again, in its cheapest form.
        /// </remarks>
        public bool ReadyPending => ReadySent && !IsLocallyReady;

        /// <summary>Whether the host may begin.</summary>
        public bool CanStart => IsHost && HasSave && Ready == roster.Count && roster.Count > 0;

        public int Ready
        {
            get
            {
                var ready = 0;
                for (var i = 0; i < roster.Count; i++)
                {
                    if (roster[i].Ready)
                    {
                        ready++;
                    }
                }
                return ready;
            }
        }

        public int Members => roster.Count;

        /// <summary>The selected save, named the way the player named it.</summary>
        public string SaveLine =>
            HasSave
                ? "Save · " + LobbySaveNames.DisplayFor(SaveKey)
                : "Save · none chosen yet";

        public string ReadyLabel => IsLocallyReady ? "Not ready" : "Ready";

        /// <summary>One line per member, capped, with the local one marked.</summary>
        public IReadOnlyList<string> RosterLines
        {
            get
            {
                if (roster.Count == 0)
                {
                    return NoLines;
                }

                // One slot of the cap is spent on the overflow line itself, so a
                // roster one over the cap does not report "…and 0 more".
                var truncated = roster.Count > MaxRosterLines;
                var shown = truncated ? MaxRosterLines - 1 : roster.Count;

                var lines = new List<string>(truncated ? MaxRosterLines : roster.Count);
                for (var i = 0; i < shown; i++)
                {
                    var peer = roster[i];

                    // Nothing stops two members sharing a name, and a roster you
                    // cannot find yourself in is one you cannot act on.
                    var who = string.IsNullOrEmpty(peer.Name)
                        ? "peer " + peer.PeerId.ToString(CultureInfo.InvariantCulture)
                        : peer.Name;
                    if (peer.PeerId == LocalPeerId)
                    {
                        who += " (you)";
                    }

                    lines.Add(who + (peer.Ready ? " — ready" : " — not ready"));
                }

                if (truncated)
                {
                    var rest = roster.Count - shown;
                    lines.Add("…and " + rest.ToString(CultureInfo.InvariantCulture) + " more");
                }
                return lines;
            }
        }

        /// <summary>
        /// Where the lobby stands, in one line, refreshed every frame.
        /// </summary>
        /// <remarks>
        /// Most fundamental first, the ordering <c>ConnectForm</c> uses: there is
        /// no point counting readiness before there is a session, and none before
        /// there is something to be ready for. A client is never told to choose a
        /// save, because it has no button for that.
        /// </remarks>
        public string StatusLine
        {
            get
            {
                if (roster.Count == 0)
                {
                    return "Not connected.";
                }
                if (!HasSave)
                {
                    return IsHost
                        ? "Choose a save to begin."
                        : "Waiting for the host to choose a save.";
                }

                var ready = Ready;
                var waiting = roster.Count - ready;
                if (waiting == 0)
                {
                    return "Everyone is ready.";
                }

                return ready.ToString(CultureInfo.InvariantCulture)
                    + " of " + roster.Count.ToString(CultureInfo.InvariantCulture)
                    + " ready · waiting for " + waiting.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
