using System;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The connect screen: address, port, passphrase, a remember tickbox, and
    // Host / Join. Opened by the Multiplayer entry on the title menu.
    //
    // Built from widgets cloned out of CIViewReporter, which the ui-dump proved
    // is INACTIVE at rest — so Instantiate does not run Awake, clones can be
    // configured before they wake, and CIViewReporter.ins is never hijacked.
    // Everything this relies on was observed on a running game, not inferred;
    // see docs/notes/ngui-surface.md.
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
        private const float CloseX = PanelWidth - 70f;
        private const float CloseY = -14f;

        private static GameObject? root;
        private static UIInput? addressField;
        private static UIInput? portField;
        private static UIInput? passphraseField;
        private static CIButton? rememberButton;
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

            CloneButton(reporter.buttonCategoryBug, ConnectText.HostButton(), ButtonX, y, OnHost);
            CloneButton(reporter.buttonCategoryFeedback, ConnectText.JoinButton(), ButtonX + ButtonSpacing, y, OnJoin);
            y -= ButtonsToStatus;

            statusLabel = CloneLabel(reporter.labelMain, string.Empty, 0f, y);

            CloneButton(reporter.buttonExit, string.Empty, CloseX, CloseY, Close);

            SendToBack(background);
            return true;
        }

        private static UIWidget? CloneBackground(CIViewReporter reporter)
        {
            var donor = FindBackgroundDonor(reporter);
            var clone = Clone(donor == null ? null : donor.gameObject, -PanelPad, PanelPad);
            if (clone == null)
            {
                return null;
            }

            // A background is decoration; anything it drags along is not.
            foreach (var child in clone.GetComponentsInChildren<Transform>(true))
            {
                if (child != clone.transform)
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }

            var widget = clone.GetComponent<UIWidget>();
            if (widget != null)
            {
                widget.rawPivot = UIWidget.Pivot.TopLeft;
                widget.width = PanelWidth;
                widget.height = PanelHeight;

                // Darkened rather than left at the donor's tint. The title menu
                // sits over a bright, busy render, and the game's own menu strip
                // solves the same problem the same way — near-black at high
                // opacity, cool rather than neutral so it reads as part of the
                // UI instead of a hole punched in it. Without this the fields
                // are unreadable over the artwork.
                // Near-opaque, not merely dark: the menu render swings from a
                // dim skyline to a full-screen explosion, and at 0.88 the panel
                // washed out to light grey on the bright frames.
                widget.color = new Color(0.03f, 0.04f, 0.06f, 0.97f);
            }

            return widget;
        }

        /// <summary>
        /// A sprite worth using as a panel backing.
        /// </summary>
        /// <remarks>
        /// By path first, because that is the one observed in the dump, then by
        /// name, then anything — a background is the one piece where the wrong
        /// sprite still beats none at all, since its job is opacity.
        /// </remarks>
        private static Transform? FindBackgroundDonor(CIViewReporter reporter)
        {
            var byPath = reporter.transform.Find("Container/Sprite_Background_Main");
            if (byPath != null)
            {
                return byPath;
            }

            UISprite? fallback = null;
            foreach (var sprite in reporter.GetComponentsInChildren<UISprite>(true))
            {
                if (sprite.name.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return sprite.transform;
                }

                if (fallback == null)
                {
                    fallback = sprite;
                }
            }

            return fallback == null ? null : fallback.transform;
        }

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

        /// <summary>
        /// Layers our own widgets without touching anyone else's depths.
        /// </summary>
        /// <remarks>
        /// NGUI orders by depth within a panel, and ours share the title menu's
        /// panel. Taking the panel two below our own minimum and the field boxes
        /// one below puts both behind our content, in the right order relative
        /// to each other, and leaves the menu's own ordering exactly as the game
        /// set it.
        /// </remarks>
        private static void SendToBack(UIWidget? background)
        {
            if (root == null)
            {
                return;
            }

            var lowest = int.MaxValue;
            foreach (var widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == background || fieldBackings.Contains(widget))
                {
                    continue;
                }

                if (widget.depth < lowest)
                {
                    lowest = widget.depth;
                }
            }

            if (lowest == int.MaxValue)
            {
                lowest = 0;
            }

            foreach (var backing in fieldBackings)
            {
                backing.depth = lowest - 1;
            }

            if (background != null)
            {
                background.depth = lowest - 2;
            }
        }

        private static UILabel? CloneLabel(UILabel? donor, string text, float x, float y)
        {
            var clone = Clone(donor == null ? null : donor.gameObject, x, y);
            if (clone == null)
            {
                return null;
            }

            // The only UILabel CIViewReporter exposes directly is the one its
            // UIInput renders through — it is registered as the "input" nav node.
            // A clone therefore arrives carrying a UIInput, whose UpdateLabel
            // overwrites the text with the donor's placeholder the moment the
            // screen is activated. That is what put "Default feedback text. You
            // can click here to edit the message." on every caption.
            //
            // Stripped by component rather than by picking a different donor, so
            // this stays right whichever label the field happens to point at.
            foreach (var input in clone.GetComponents<UIInput>())
            {
                UnityEngine.Object.DestroyImmediate(input);
            }

            foreach (var collider in clone.GetComponents<BoxCollider>())
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            var label = clone.GetComponent<UILabel>();
            if (label != null)
            {
                label.text = text;
                label.width = PanelWidth - (int)(PanelPad * 2);
                label.multiLine = true;
                label.overflowMethod = UILabel.Overflow.ResizeHeight;
            }

            return label;
        }

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

        private static CIButton? CloneButton(CIButton? donor, string text, float x, float y, Action onClick)
        {
            var clone = Clone(donor == null ? null : donor.gameObject, x, y);
            if (clone == null)
            {
                return null;
            }

            var button = clone.GetComponent<CIButton>();
            if (button == null)
            {
                return null;
            }

            button.callbackOnClick = new UICallback(onClick);
            button.tooltipHeader = text;
            button.tooltipContent = string.Empty;

            if (text.Length > 0)
            {
                var label = clone.GetComponentInChildren<UILabel>(true);
                if (label != null)
                {
                    label.text = text;
                }
            }

            return button;
        }

        private static GameObject? Clone(GameObject? donor, float x, float y)
        {
            if (donor == null || root == null)
            {
                return null;
            }

            try
            {
                var clone = UnityEngine.Object.Instantiate(donor, root.transform);
                clone.layer = 5;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                clone.transform.localPosition = new Vector3(x, y, 0f);
                clone.SetActive(true);

                StripLocalizers(clone);
                return clone;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not clone " + donor.name + ": " + e.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// Removes the game's localization component from a cloned subtree.
        /// </summary>
        /// <remarks>
        /// CILabel.Start assigns label.text from the text library, and
        /// DataManagerText re-applies every registered one on a language change,
        /// so anything we write would revert to the donor's wording — on the
        /// frame after activation, which is the worst kind of bug to chase.
        /// DestroyImmediate rather than Destroy because Destroy is deferred to
        /// the end of the frame, and the root may be activated before then.
        /// </remarks>
        private static void StripLocalizers(GameObject subtree)
        {
            foreach (var label in subtree.GetComponentsInChildren<CILabel>(true))
            {
                UnityEngine.Object.DestroyImmediate(label);
            }
        }

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
