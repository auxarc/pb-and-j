using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // M11c: the lobby. The connect screen is the door — address, port,
    // passphrase — and this is the room behind it: which save the session will
    // play, who is here, and who has agreed.
    //
    // Deliberately NOT auto-opened when a session starts. M10's close-out rejected
    // auto-closing the connect screen for the same reason: a screen that changes
    // by itself cannot be told apart from one that failed, and "did my connection
    // work?" is exactly the question the connect screen exists to answer. The
    // player opens this when they are ready to.
    //
    // Humble object: every word and every rule about who may do what lives in
    // PBAndJ.Core's LobbyView and LobbyText under the 100% gate. This file
    // positions widgets and shuttles strings. The cloning primitives are shared
    // with the connect screen via NguiKit — one copy of the traps, not two.
    [ExcludeFromCodeCoverage]
    internal static class LobbyScreenGlue
    {
        private const float PanelX = 660f;
        private const float PanelY = 90f;

        private const int PanelWidth = 440;
        // 540 and seven roster lines put the button row at y=-454 against a panel
        // bottom of -516 — the same fit the connect screen was verified at. Nine
        // lines pushed the row to -514, eighteen units BELOW the backing, where
        // the buttons float over raw menu art. The horizontal clearance comment
        // below was copied from that screen; the vertical fit was not, and had to
        // be worked out rather than assumed.
        private const int PanelHeight = 540;
        private const float PanelPad = 24f;

        private const float TitleToSave = 56f;
        private const float SaveToChoose = 40f;
        private const float ChooseToRoster = 62f;
        private const float RosterStep = 30f;
        private const float RosterToStatus = 26f;
        private const float StatusToButtons = 60f;

        // The cloned push-buttons draw from their own centre, not their top-left
        // like the labels do — the same 58-unit offset the connect screen
        // measured, so the same clearance works here.
        private const float ButtonX = 116f;
        private const float ButtonSpacing = 190f;
        private const float CloseX = PanelWidth - 70f;
        private const float CloseY = -14f;

        private static GameObject? root;
        private static UILabel? saveLabel;
        private static UILabel? statusLabel;
        private static CIButton? chooseButton;
        private static CIButton? readyButton;
        private static CIButton? startButton;
        private static UILabel? readyLabel;
        private static readonly List<UILabel> rosterLabels = new List<UILabel>();

        private static bool built;
        private static string notice = string.Empty;

        internal static bool IsOpen => root != null && root.activeSelf;

        // --- opening and closing ---

        internal static void Open()
        {
            try
            {
                if (IsOpen)
                {
                    Close();
                    return;
                }

                if (!NetGlue.HasSession)
                {
                    Tell(LobbyText.NoSession());
                    return;
                }

                // Same courtesy the Multiplayer button does: the game's own pages
                // close each other through TryExitSubordinates, a mechanism our
                // screens cannot join, so without this the lobby stacks on top of
                // whatever page is open.
                CIViewPauseRoot.ins?.TryExitSubordinates();
                ConnectScreenGlue.Close();

                if (!Build())
                {
                    Tell(LobbyText.CouldNotBuild());
                    return;
                }

                notice = string.Empty;
                Refresh();
                root!.SetActive(true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not open the lobby screen: " + e);
                Tell(LobbyText.CouldNotBuild());
            }
        }

        /// <summary>
        /// The player is done with the lobby. Ends any picking with it.
        /// </summary>
        /// <remarks>
        /// PickMode is session-global and a stuck one turns Title → Load into a
        /// multiplayer-only list, so a real close clears it — one of the three
        /// independent recoveries.
        /// <para>
        /// <b>Distinct from <see cref="Hide"/>, and the distinction is load-bearing.</b>
        /// The picker hides this screen while it borrows the load screen; if that
        /// went through here it would switch pick mode off one line after
        /// <see cref="LobbyPicker"/> switched it on, and the "picker" would open
        /// showing the singleplayer catalogue with a live Confirm behind it.
        /// </para>
        /// </remarks>
        internal static void Close()
        {
            SaveVisibilityPatches.ExitPickMode();
            Hide();
        }

        /// <summary>Takes the screen off without ending the lobby flow.</summary>
        internal static void Hide()
        {
            try
            {
                if (root != null)
                {
                    root.SetActive(false);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not hide the lobby screen: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// Puts the screen back after the picker, without <see cref="Open"/>'s
        /// toggle.
        /// </summary>
        /// <remarks>
        /// <see cref="Open"/> closes when already open, which is right for a
        /// button and wrong for a restore — a restore that toggled would close the
        /// screen it was asked to bring back.
        /// </remarks>
        internal static void Reopen()
        {
            if (IsOpen)
            {
                return;
            }
            Open();
        }

        /// <summary>
        /// Whether the screen has been built and still exists.
        /// </summary>
        /// <remarks>
        /// True while the picker has it hidden, which is what makes it the right
        /// question for the pick-mode owner check. "Is it visible" is not: the
        /// picker deliberately hides it, so that answer would disarm pick mode on
        /// the picker's very first grid rebuild.
        /// </remarks>
        internal static bool Exists => root != null;

        /// <summary>
        /// Per-frame upkeep, from the existing Heartbeat pump.
        /// </summary>
        /// <remarks>
        /// Redrawn every frame rather than on change, exactly as the connect
        /// screen ended up doing. A lobby that had accepted your ready and one
        /// that had dropped it must never look the same, and the cheapest way to
        /// guarantee that is to never cache what the session says.
        /// </remarks>
        internal static void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            try
            {
                // The session can end from anywhere — a kick, a timeout, the
                // console. A lobby with no session behind it shows a fiction.
                if (!NetGlue.HasSession)
                {
                    Close();
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    return;
                }

                // Leaving the title menu takes the screen with it — the same exit
                // the connect screen needed, and for the same reason.
                if (CIViewPauseRoot.ins != null && !CIViewPauseRoot.ins.mainMode)
                {
                    Close();
                    return;
                }

                Refresh();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] lobby screen fault, closing: " + e.GetType().Name);
                Close();
            }
        }

        // --- actions ---

        private static void OnChooseSave()
        {
            var view = NetGlue.LobbyView();
            if (view == null || !view.CanChooseSave)
            {
                return;
            }

            LobbyPicker.Open();
        }

        private static void OnReadyToggle()
        {
            var view = NetGlue.LobbyView();
            if (view == null || !view.CanReady)
            {
                // Say why rather than swallowing the click. A button that does
                // nothing and explains nothing is the bug the connect screen paid
                // for, and HandleLocalLobbyReady refuses this case in silence.
                notice = LobbyText.NothingToAgreeTo();
                return;
            }

            notice = string.Empty;
            if (view.IsLocallyReady)
            {
                NetGlue.PostLocalLobbyUnready();
            }
            else
            {
                NetGlue.PostLocalLobbyReady();
            }
        }

        private static void OnStart()
        {
            // Present, pressable, and honest about being unfinished. Hiding it
            // once everyone is ready would make a full lobby indistinguishable
            // from a broken one; wiring it to nothing would be M6 again.
            notice = LobbyText.StartNotImplemented();
        }

        // --- drawing ---

        private static void Refresh()
        {
            var view = NetGlue.LobbyView();
            if (view == null)
            {
                return;
            }

            if (saveLabel != null)
            {
                saveLabel.text = view.SaveLine;
            }

            NguiKit.Show(chooseButton, view.CanChooseSave);
            NguiKit.Show(startButton, view.CanStart);

            if (readyLabel != null)
            {
                readyLabel.text = view.ReadyLabel;
            }

            var lines = view.RosterLines;
            for (var i = 0; i < rosterLabels.Count; i++)
            {
                rosterLabels[i].text = i < lines.Count ? lines[i] : string.Empty;
            }

            if (statusLabel != null)
            {
                var line = view.StatusLine;
                if (view.ReadyPending)
                {
                    line += LobbyText.ReadyPendingSuffix();
                }
                if (notice.Length > 0)
                {
                    line += "\n" + notice;
                }
                statusLabel.text = line;
            }
        }

        /// <summary>
        /// Called by <see cref="LobbyPicker"/> once the player has chosen.
        /// </summary>
        internal static void OnSavePicked(string key)
        {
            notice = NetGlue.PostLocalLobbySelect(key) ? string.Empty : LobbyText.SaveNotUsable();
        }

        /// <summary>
        /// Whether a lobby is still a live concern — the question the pick-mode
        /// recovery asks.
        /// </summary>
        /// <remarks>
        /// The session half is what gives this teeth. The screen object survives
        /// once built, so on its own it would answer yes forever; but pick mode is
        /// meaningless without a session, and a session ending is exactly the
        /// event that could otherwise strand the bit somewhere no exit path runs.
        /// </remarks>
        internal static bool IsPickingPlausible() => NetGlue.HasSession && Exists;

        // --- construction ---

        private static bool Build()
        {
            if (built)
            {
                return root != null;
            }

            var reporter = CIViewReporter.ins;
            var parent = CIViewPauseRoot.ins?.transform;
            if (reporter == null || reporter.labelMain == null || parent == null)
            {
                // Deliberately does NOT latch `built`. Opening the lobby is
                // something the player asked for, so a retry costs nothing and a
                // one-off null during a view transition should not disable the
                // screen for the rest of the process.
                return false;
            }

            built = true;

            root = new GameObject("pbj_lobby_screen");
            root.layer = 5;
            root.transform.parent = parent;
            root.transform.localPosition = new Vector3(PanelX, PanelY, 0f);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // Assembled while inactive, so no cloned Awake or Start has run and
            // CILabel can be stripped before it can overwrite anything.
            root.SetActive(false);

            var background = NguiKit.CloneBackground(root, reporter, PanelWidth, PanelHeight, PanelPad);
            var width = PanelWidth - (int)(PanelPad * 2);

            var y = 0f;
            Label(reporter, LobbyText.Title(), y, width);
            y -= TitleToSave;

            saveLabel = Label(reporter, string.Empty, y, width);
            y -= SaveToChoose;

            chooseButton = NguiKit.CloneButton(
                root, reporter.buttonCategoryBug, LobbyText.ChooseSaveLabel(), ButtonX, y, OnChooseSave);
            y -= ChooseToRoster;

            for (var i = 0; i < LobbyView.MaxRosterLines; i++)
            {
                var line = Label(reporter, string.Empty, y, width);
                if (line != null)
                {
                    rosterLabels.Add(line);
                }
                y -= RosterStep;
            }

            y -= RosterToStatus;
            statusLabel = Label(reporter, string.Empty, y, width);
            y -= StatusToButtons;

            readyButton = NguiKit.CloneButton(
                root, reporter.buttonCategoryFeedback, LobbyText.ReadyLabel(), ButtonX, y, OnReadyToggle);
            if (readyButton != null)
            {
                // Cached so the toggle's wording can change without re-finding it
                // every frame.
                readyLabel = readyButton.GetComponentInChildren<UILabel>(true);
            }

            startButton = NguiKit.CloneButton(
                root, reporter.buttonCategorySuggestion, LobbyText.StartLabel(),
                ButtonX + ButtonSpacing, y, OnStart);

            NguiKit.CloneButton(root, reporter.buttonExit, string.Empty, CloseX, CloseY, Close);

            NguiKit.SendToBack(root, background, null);
            return true;
        }

        private static UILabel? Label(CIViewReporter reporter, string text, float y, int width) =>
            NguiKit.CloneLabel(root, reporter.labelMain, text, 0f, y, width);

        // --- the console command, and the fallback ---

        public static string Lobby()
        {
            Open();
            return IsOpen ? LobbyText.Opened() : LobbyText.NotOpened();
        }

        /// <summary>
        /// Says something when the screen cannot be shown, so a failure is never
        /// silent.
        /// </summary>
        private static void Tell(string message)
        {
            Debug.Log("[pb-and-j] " + message);
            try
            {
                CIViewDialogConfirmation.ins?.Open(LobbyText.Title(), message, null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not show the dialog: " + e.GetType().Name);
            }
        }
    }
}
