using System;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The connect screen: address, port, passphrase, a remember tickbox, and
    // Host / Join — which give way to Leave once a session is live, so starting
    // and stopping one both live here rather than half here and half in the
    // console.
    //
    // Built from widgets cloned out of CIViewReporter, which is INACTIVE at rest
    // — so Instantiate does not run Awake, clones can be configured before they
    // wake, and CIViewReporter.ins is never hijacked. Everything this relies on
    // was observed on a running game, not inferred; docs/notes/ngui-surface.md
    // is the record, and now the only one — the probes that produced it have
    // been deleted.
    //
    // Humble object: every rule and every word lives in PBAndJ.Core
    // (ConnectForm, ConnectRules, ConnectSettings, ConnectText) under the 100%
    // gate. This file positions widgets and shuttles strings.
    [ExcludeFromCodeCoverage]
    internal static class ConnectScreenGlue
    {
        // All geometry in one place — it is the part that gets adjusted by eye.
        private const float PanelX = 660f;
        private const float PanelY = 90f;

        // A row is a caption plus a field box, so it cannot be shorter than
        // LabelToField + the box height, or the next row lands on top of the
        // previous one's field. That is what put the tickbox on the passphrase.
        private const float RowStep = 72f;
        private const float LabelToField = 30f;
        private const float ToggleToWarning = 44f;
        private const float WarningToButtons = 96f;
        private const float ButtonsToStatus = 56f;

        private const int FieldWidth = 340;
        private const int FieldHeight = 30;
        private const int FieldBoxPad = 10;
        private const int FieldFontSize = 20;

        private const int PanelWidth = 440;
        private const int PanelHeight = 540;
        private const float PanelPad = 24f;

        // The cloned push-buttons draw from their own centre, not their top-left
        // like the labels do, so one at x=0 hangs off the panel's left edge.
        // Measured against the panel rather than guessed: the button art sits
        // about 58 units left of its transform, so this clears the edge and the
        // pad. The tickbox donor is already top-left aligned and needs none.
        private const float ButtonX = 116f;
        private const float ButtonSpacing = 190f;

        // Leave and Lobby take the pair's place rather than sitting beside them,
        // so they reuse Host's and Join's slots exactly. Leave was centred between
        // the two while it was alone; M11c gave it company, and a pair the panel
        // already fits beats inventing a third position.
        private const float LeaveX = ButtonX;
        private const float LobbyX = ButtonX + ButtonSpacing;
        private const float CloseX = PanelWidth - 70f;
        private const float CloseY = -14f;

        private static GameObject? root;
        private static UIInput? addressField;
        private static UIInput? portField;
        private static UIInput? passphraseField;
        private static CIButton? rememberButton;
        private static CIButton? hostButton;
        private static CIButton? joinButton;
        private static CIButton? leaveButton;
        private static CIButton? lobbyButton;
        private static UILabel? statusLabel;

        private static readonly System.Collections.Generic.List<UIWidget> fieldBackings =
            new System.Collections.Generic.List<UIWidget>();

        private static readonly ConnectForm Form = new ConnectForm();
        private static bool remember;
        private static bool built;
        private static string status = string.Empty;

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

                if (!Build())
                {
                    // Cloning failed. The dialog path still works and still
                    // beats sending somebody to the console.
                    Fallback();
                    return;
                }

                LoadIntoForm();
                PushToWidgets();
                ApplySessionState();
                status = string.Empty;

                root!.SetActive(true);
                addressField?.ForceSelection(true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not open the multiplayer screen: " + e);
                Fallback();
            }
        }

        internal static void Close()
        {
            try
            {
                if (root == null)
                {
                    return;
                }

                addressField?.RemoveFocus();
                portField?.RemoveFocus();
                passphraseField?.RemoveFocus();
                root.SetActive(false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not close the multiplayer screen: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// Per-frame upkeep, driven from the existing Heartbeat.Update pump
        /// rather than a MonoBehaviour of our own — that pump already runs in
        /// every game state including the main menu.
        /// </summary>
        internal static void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            try
            {
                // The dev console and an NGUI input both poll raw input every
                // frame and neither knows the other exists, so with both up,
                // every keystroke lands in both — the console's own toggle key
                // included. Observed, not theorised. Yield focus to the console.
                if (QuantumConsole.Instance != null && QuantumConsole.Instance.IsActive)
                {
                    if (UIInput.selection != null && IsOurs(UIInput.selection))
                    {
                        UIInput.selection.RemoveFocus();
                    }
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    return;
                }

                // Leaving the title menu takes the screen with it.
                if (CIViewPauseRoot.ins != null && !CIViewPauseRoot.ins.mainMode)
                {
                    Close();
                    return;
                }

                PullFromWidgets();
                ApplySessionState();
                RefreshStatus();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] multiplayer screen fault, closing: " + e.GetType().Name);
                Close();
            }
        }

        private static bool IsOurs(UIInput candidate)
        {
            return candidate == addressField || candidate == portField || candidate == passphraseField;
        }

        // --- actions ---

        private static void OnHost()
        {
            PullFromWidgets();

            if (!Form.CanHost)
            {
                Say(ConnectText.DescribeProblem(Form.HostProblem));
                return;
            }

            SaveForm();
            Say(ConnectText.Hosting(Form.AddressText, Form.Port));

            // Shown, not logged: NetGlue already logs this line itself, and the
            // whole point of the screen is that nobody should have to read the
            // log to find out whether it worked.
            Say(ConnectText.ForScreen(NetGlue.Host(Form.AddressText, Form.Port, Form.Passphrase)));
        }

        private static void OnJoin()
        {
            PullFromWidgets();

            if (!Form.CanJoin)
            {
                Say(ConnectText.DescribeProblem(Form.JoinProblem));
                return;
            }

            SaveForm();
            Say(ConnectText.Joining(Form.AddressText, Form.Port));
            Say(ConnectText.ForScreen(NetGlue.Join(Form.AddressText, Form.Port, Form.Passphrase)));
        }

        /// <summary>
        /// Ends the session without sending anybody to the console.
        /// </summary>
        /// <remarks>
        /// This is what the screen was missing: it could start a session and
        /// could not stop one, so the only way out was <c>pbj.net-stop</c> —
        /// which is exactly the audience the screen exists to spare.
        /// </remarks>
        private static void OnLobby()
        {
            // Opening the lobby closes this screen — LobbyScreenGlue.Open does it,
            // so the two cannot end up stacked. Deliberately a click and not an
            // automatic transition on connect: see ConnectText.LobbyButton.
            LobbyScreenGlue.Open();
        }

        private static void OnLeave()
        {
            // NetStop composes its own sentence and names the peer count, so
            // there is nothing to add; it just arrives wearing the log marker.
            Say(ConnectText.ForScreen(NetGlue.NetStop()));
            ApplySessionState();
        }

        /// <summary>
        /// Shows the actions that can actually be taken right now.
        /// </summary>
        /// <remarks>
        /// Host and Join give way to Leave while a session is live. NetGlue
        /// refuses a second session regardless, so this is not what makes the
        /// mod safe — it is what stops the screen offering a button whose only
        /// possible outcome is a refusal.
        /// <para>
        /// Hiding rather than dimming. CIButton.available does block the click
        /// and dim the art, but its disabled-click path reads
        /// <c>audio.enabled</c> before any null check
        /// (CombatUI/CIButton.cs:529), and a clone's audio block is whatever the
        /// donor happened to serialize. A deactivated GameObject never reaches
        /// that code, and hiding is what this screen wants anyway.
        /// </para>
        /// </remarks>
        private static void ApplySessionState()
        {
            var running = NetGlue.HasSession;
            Form.SessionRunning = running;

            Show(hostButton, !running);
            Show(joinButton, !running);
            Show(leaveButton, running);
            Show(lobbyButton, running);
        }

        private static void Show(CIButton? button, bool visible) =>
            NguiKit.Show(button, visible);

        private static void OnToggleRemember()
        {
            if (rememberButton != null)
            {
                rememberButton.SetToggle(ref remember, !remember);
            }
            else
            {
                remember = !remember;
            }

            Form.RememberPassphrase = remember;
        }

        private static void Say(string line)
        {
            status = line;
            if (statusLabel != null)
            {
                statusLabel.text = line;
            }
        }

        /// <summary>
        /// Replaces the status line with whatever the session is actually doing,
        /// so a refusal names the reason instead of leaving "Joining…" up.
        /// </summary>
        private static void RefreshStatus()
        {
            var rejection = NetGlue.LastRejection();
            if (rejection != null)
            {
                Say(ConnectText.DescribeRejection(rejection.Value));
                return;
            }

            // A live session reports itself every frame. Without this the screen
            // sat on "Joining …" after a handshake had already succeeded, which
            // reads as a failure — the connection worked and looked like it had
            // not.
            if (NetGlue.HasSession)
            {
                Say(ConnectText.ForScreen(NetGlue.NetStatus()));
                return;
            }

            if (statusLabel != null && statusLabel.text != status)
            {
                statusLabel.text = status;
            }
        }

        // --- form <-> widgets ---

        private static void LoadIntoForm()
        {
            var stored = ConnectSettingsStore.Load();
            var loaded = ConnectForm.FromSettings(stored);

            Form.AddressText = loaded.AddressText;
            Form.PortText = loaded.PortText;
            Form.Passphrase = loaded.Passphrase;
            Form.RememberPassphrase = loaded.RememberPassphrase;
            remember = loaded.RememberPassphrase;
        }

        private static void PushToWidgets()
        {
            if (addressField != null) addressField.value = Form.AddressText;
            if (portField != null) portField.value = Form.PortText;
            if (passphraseField != null) passphraseField.value = Form.Passphrase;
            if (rememberButton != null) rememberButton.SetToggle(ref remember, remember);
        }

        private static void PullFromWidgets()
        {
            if (addressField != null) Form.AddressText = addressField.value;
            if (portField != null) Form.PortText = portField.value;
            if (passphraseField != null) Form.Passphrase = passphraseField.value;
            Form.RememberPassphrase = remember;
        }

        private static void SaveForm()
        {
            ConnectSettingsStore.Save(Form.ToSettings());
        }

        // --- construction ---

        private static bool Build()
        {
            if (built)
            {
                return root != null;
            }

            built = true;

            var reporter = CIViewReporter.ins;
            var parent = CIViewPauseRoot.ins?.transform;
            if (reporter == null || reporter.input == null || parent == null)
            {
                return false;
            }

            root = new GameObject("pbj_connect_screen");
            root.layer = 5;
            root.transform.parent = parent;
            root.transform.localPosition = new Vector3(PanelX, PanelY, 0f);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // Everything is assembled while the root is inactive, so no cloned
            // Awake or Start has run yet and CILabel can be removed before it
            // can overwrite anything.
            root.SetActive(false);

            // Behind everything else, so the panel is readable over the menu's
            // artwork rather than fighting it.
            var background = CloneBackground(reporter);

            var y = 0f;

            CloneLabel(reporter.labelMain, ConnectText.Title(), 0f, y);
            y -= RowStep;

            CloneLabel(reporter.labelMain, ConnectText.AddressLabel(), 0f, y);
            CloneFieldBacking(reporter, y - LabelToField);
            addressField = CloneField(reporter.input, 0f, y - LabelToField, UIInput.InputType.Standard);
            y -= RowStep;

            CloneLabel(reporter.labelMain, ConnectText.PortLabel(), 0f, y);
            CloneFieldBacking(reporter, y - LabelToField);
            portField = CloneField(reporter.input, 0f, y - LabelToField, UIInput.InputType.Standard);
            if (portField != null)
            {
                portField.validation = UIInput.Validation.Integer;
                portField.characterLimit = 5;
            }
            y -= RowStep;

            CloneLabel(reporter.labelMain, ConnectText.PassphraseLabel(), 0f, y);
            CloneFieldBacking(reporter, y - LabelToField);
            passphraseField = CloneField(reporter.input, 0f, y - LabelToField, UIInput.InputType.Password);
            y -= RowStep;

            rememberButton = CloneButton(
                reporter.buttonToggleIncludeSave, ConnectText.RememberLabel(), 0f, y, OnToggleRemember);
            y -= ToggleToWarning;

            CloneLabel(reporter.labelMain, ConnectText.RememberWarning(), 0f, y);
            y -= WarningToButtons;

            hostButton = CloneButton(
                reporter.buttonCategoryBug, ConnectText.HostButton(), ButtonX, y, OnHost);
            joinButton = CloneButton(
                reporter.buttonCategoryFeedback, ConnectText.JoinButton(), ButtonX + ButtonSpacing, y, OnJoin);

            // The one category button the other two did not take, so Leave gets
            // its own donor and matches their look without new recon.
            leaveButton = CloneButton(
                reporter.buttonCategorySuggestion, ConnectText.LeaveButton(), LeaveX, y, OnLeave);

            // Re-clones Host's donor. A donor can be Instantiated any number of
            // times, and a fourth category button does not exist to borrow.
            lobbyButton = CloneButton(
                reporter.buttonCategoryBug, ConnectText.LobbyButton(), LobbyX, y, OnLobby);
            y -= ButtonsToStatus;

            statusLabel = CloneLabel(reporter.labelMain, string.Empty, 0f, y);

            CloneButton(reporter.buttonExit, string.Empty, CloseX, CloseY, Close);

            SendToBack(background);
            return true;
        }

        private static UIWidget? CloneBackground(CIViewReporter reporter) =>
            NguiKit.CloneBackground(root, reporter, PanelWidth, PanelHeight, PanelPad);

        private static Transform? FindBackgroundDonor(CIViewReporter reporter) =>
            NguiKit.FindBackgroundDonor(reporter);

        /// <summary>
        /// Gives a text field a visible box to click in.
        /// </summary>
        /// <remarks>
        /// Without one the fields are invisible until they contain text, and an
        /// empty screen with three unmarked gaps in it is not a form.
        /// </remarks>
        private static void CloneFieldBacking(CIViewReporter reporter, float y)
        {
            var donor = reporter.transform.Find("Container/Container_Comment/Sprite_Background_Input");

            // Drawn around the field's centre line, so the text sits inside it
            // rather than on its top edge.
            var height = FieldHeight + FieldBoxPad;
            var clone = Clone(donor == null ? null : donor.gameObject, -FieldBoxPad, y + (height / 2f));
            if (clone == null)
            {
                return;
            }

            var widget = clone.GetComponent<UIWidget>();
            if (widget == null)
            {
                return;
            }

            widget.rawPivot = UIWidget.Pivot.TopLeft;
            widget.width = FieldWidth + (FieldBoxPad * 2);
            widget.height = height;
            widget.color = new Color(1f, 1f, 1f, 0.16f);

            fieldBackings.Add(widget);
        }

        private static void SendToBack(UIWidget? background) =>
            NguiKit.SendToBack(root, background, fieldBackings);

        private static UILabel? CloneLabel(UILabel? donor, string text, float x, float y) =>
            NguiKit.CloneLabel(root, donor, text, x, y, PanelWidth - (int)(PanelPad * 2));

        private static UIInput? CloneField(UIInput donor, float x, float y, UIInput.InputType type)
        {
            var clone = Clone(donor.gameObject, x, y);
            if (clone == null)
            {
                return null;
            }

            var input = clone.GetComponent<UIInput>();
            if (input == null)
            {
                return null;
            }

            // The label is styled BEFORE anything on the input is assigned.
            // UIInput.Init caches the label's alignment into mAlignment, and
            // UpdateLabel restores from that cache on every redraw — so styling
            // afterwards is quietly reverted. Init is triggered by the first
            // property setter touched below.
            var label = clone.GetComponent<UILabel>();
            if (label != null)
            {
                label.width = FieldWidth;
                label.height = FieldHeight;
                label.multiLine = false;
                label.overflowMethod = UILabel.Overflow.ClampContent;

                // Pivoted on the left edge so the text sits on the transform's
                // vertical centre, which is what the box is drawn around. The
                // donor is a multi-line comment box anchored at its top, which
                // left the text riding the top edge of the field.
                label.rawPivot = UIWidget.Pivot.Left;
                label.alignment = NGUIText.Alignment.Left;
                label.fontSize = FieldFontSize;
            }

            // The donor's serialized handler points at the game's bug reporter,
            // and a clone inherits it — every keystroke would call in there.
            input.onChange.Clear();

            // NGUI's own PlayerPrefs autosave. It would persist a passphrase
            // unconditionally, with no tickbox and no warning.
            input.savedAs = string.Empty;

            input.inputType = type;
            input.value = string.Empty;
            input.characterLimit = 64;
            input.submitOnUnselect = true;

            // No placeholder is set: UIInput.UpdateLabel renders a hardcoded
            // em-dash for an empty unselected field and never reads defaultText
            // on that path. The hint lives on the caption instead.

            var collider = clone.GetComponent<BoxCollider>();
            if (collider != null)
            {
                collider.size = new Vector3(FieldWidth, FieldHeight + FieldBoxPad, 0f);
                collider.center = new Vector3(FieldWidth / 2f, 0f, 0f);
            }

            return input;
        }

        private static CIButton? CloneButton(CIButton? donor, string text, float x, float y, Action onClick) =>
            NguiKit.CloneButton(root, donor, text, x, y, onClick);

        private static GameObject? Clone(GameObject? donor, float x, float y) =>
            NguiKit.Clone(root, donor, x, y);

        private static void StripLocalizers(GameObject subtree) =>
            NguiKit.StripLocalizers(subtree);

        // --- fallback, and the console commands ---

        /// <summary>
        /// Used when the widgets cannot be cloned: offers the remembered
        /// connection through the game's confirmation dialog.
        /// </summary>
        private static void Fallback()
        {
            try
            {
                var form = ConnectForm.FromSettings(ConnectSettingsStore.Load());

                if (!form.CanJoin)
                {
                    Tell(ConnectText.NothingRemembered());
                    return;
                }

                var address = form.AddressText;
                var port = form.Port;
                var passphrase = form.Passphrase;

                Ask(ConnectText.ConfirmJoin(address, port),
                    () => Debug.Log(NetGlue.Join(address, port, passphrase)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] multiplayer fallback failed: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// Records what a connection used, so it can be offered back later.
        /// </summary>
        /// <remarks>
        /// The passphrase is kept only if it was already being kept — the
        /// console path has no tickbox to consent with, and typing a command is
        /// not agreeing to have a shared secret written to disk.
        /// </remarks>
        internal static void Remember(string address, int port, string? passphrase)
        {
            try
            {
                var previous = ConnectSettingsStore.Load();
                var keep = previous.RememberPassphrase;

                ConnectSettingsStore.Save(new ConnectSettings(
                    address, port, keep, keep ? passphrase : null));
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] could not remember the connection: " + e.GetType().Name);
            }
        }

        public static string Connect()
        {
            Open();
            return "[pb-and-j] multiplayer screen toggled";
        }

        public static string ConnectForget()
        {
            return ConnectSettingsStore.Forget()
                ? "[pb-and-j] forgot the saved connection"
                : "[pb-and-j] could not clear the saved connection — see the log";
        }

        private static void Ask(string question, Action onConfirm)
        {
            if (CIViewDialogConfirmation.ins == null)
            {
                Debug.Log("[pb-and-j] " + question);
                return;
            }

            CIViewDialogConfirmation.ins.Open(
                ConnectText.Title(), question, () => onConfirm(), null, null, null, 0.55f);
        }

        private static void Tell(string message)
        {
            if (CIViewDialogConfirmation.ins == null)
            {
                Debug.Log("[pb-and-j] " + message);
                return;
            }

            CIViewDialogConfirmation.ins.Open(
                ConnectText.Title(), message, null, null, null, null, 0.55f, true, false);
        }
    }
}
