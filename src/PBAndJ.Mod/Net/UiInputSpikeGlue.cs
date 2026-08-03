using System;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Can we actually type at the title screen?
    //
    // THROWAWAY, like UiProbeGlue. This is the last question standing between
    // the recon and the real connect screen, and it is not answerable by
    // reading: whether a cloned UIInput takes keystrokes at the main menu
    // depends on UICamera, on Rewired, and on the game's input-context stack,
    // none of which can be evaluated statically.
    //
    // What the dump already settled, and this relies on:
    //   - CIViewReporter's GameObject is INACTIVE at rest, so Instantiate does
    //     not run Awake and the clone can be configured before it wakes up
    //   - the UIInput and its UILabel are the SAME GameObject, so one clone is a
    //     self-contained field
    //   - that UIInput carries a serialized onChange pointing at
    //     CIViewReporter.OnInputChange, which a clone inherits and must lose
    [ExcludeFromCodeCoverage]
    internal static class UiInputSpikeGlue
    {
        private const string InputContext = "SharedConfirmationDialog";

        private static GameObject? clone;
        private static UIInput? input;
        private static bool contextEntered;

        public static string UiSpikeInput()
        {
            try
            {
                if (clone != null)
                {
                    return "[pb-and-j] spike field already open — pbj.ui-spike-close first";
                }

                var donor = CIViewReporter.ins?.input;
                if (donor == null)
                {
                    return "[pb-and-j] no donor input found (CIViewReporter.ins.input is null)";
                }

                var parent = CIViewPauseRoot.ins?.transform;
                if (parent == null)
                {
                    return "[pb-and-j] no title menu to attach to — run this at the main menu";
                }

                // The donor is inactive, so the clone is inactive and no Awake
                // has run yet. Everything below happens before it wakes.
                clone = UnityEngine.Object.Instantiate(donor.gameObject, parent);
                clone.name = "pbj_spike_input";
                clone.layer = 5;

                var t = clone.transform;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                t.localPosition = new Vector3(600f, -200f, 0f);

                input = clone.GetComponent<UIInput>();
                if (input == null)
                {
                    Close();
                    return "[pb-and-j] the clone has no UIInput — donor structure has changed";
                }

                // The inherited handler points at the game's bug reporter; every
                // keystroke would call into it.
                input.onChange.Clear();

                // NGUI's own PlayerPrefs autosave. Harmless for a spike, wrong
                // for a passphrase, and cleared here so the habit is set.
                input.savedAs = string.Empty;

                input.value = string.Empty;
                input.defaultText = "type here";
                input.inputType = UIInput.InputType.Standard;

                StripLocalizers(clone);

                EventDelegate.Add(input.onChange, OnChanged);

                clone.SetActive(true);

                // Whether the clone's UIInput.label points at the clone's own
                // UILabel or still at the donor's. It should be the former —
                // they are the same GameObject — but this is exactly the kind of
                // thing that is cheap to check and expensive to assume.
                var own = clone.GetComponent<UILabel>();
                var labelIsOwn = input.label != null && input.label == own;

                input.ForceSelection(true);

                Debug.Log(
                    "[pb-and-j] spike: labelIsOwn=" + labelIsOwn
                    + " selection=" + Describe(UIInput.selection)
                    + " selectedObject=" + Describe(UICamera.selectedObject)
                    + " | click the field and type; pbj.ui-spike-status to read it back");

                return "[pb-and-j] spike field opened (labelIsOwn=" + labelIsOwn + ")";
            }
            catch (Exception e)
            {
                Close();
                return "[pb-and-j] spike failed: " + e;
            }
        }

        /// <summary>
        /// Reads the field back, plus whatever currently holds focus.
        /// </summary>
        public static string UiSpikeStatus()
        {
            if (input == null)
            {
                return "[pb-and-j] no spike field open";
            }

            return "[pb-and-j] spike value='" + input.value
                + "' isSelected=" + input.isSelected
                + " selection=" + Describe(UIInput.selection)
                + " selectedObject=" + Describe(UICamera.selectedObject)
                + " context=" + (contextEntered ? InputContext : "(none)");
        }

        /// <summary>
        /// Pushes the game's dialog input context, in case the menu is eating
        /// keystrokes. Separate from opening the field so the first run answers
        /// "does this work unaided" before anything is worked around.
        /// </summary>
        public static string UiSpikeContext()
        {
            try
            {
                if (contextEntered)
                {
                    InputHelper.ExitInputContext(InputContext);
                    contextEntered = false;
                    return "[pb-and-j] left input context " + InputContext;
                }

                InputHelper.EnterInputContext(InputContext);
                contextEntered = true;

                if (input != null)
                {
                    input.ForceSelection(true);
                }

                return "[pb-and-j] entered input context " + InputContext;
            }
            catch (Exception e)
            {
                return "[pb-and-j] could not toggle the input context: " + e.GetType().Name;
            }
        }

        public static string UiSpikeClose()
        {
            Close();
            return "[pb-and-j] spike field closed";
        }

        private static void Close()
        {
            try
            {
                if (contextEntered)
                {
                    InputHelper.ExitInputContext(InputContext);
                    contextEntered = false;
                }

                if (input != null)
                {
                    input.RemoveFocus();
                }

                if (clone != null)
                {
                    UnityEngine.Object.Destroy(clone);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] spike cleanup: " + e.GetType().Name);
            }
            finally
            {
                clone = null;
                input = null;
            }
        }

        private static void OnChanged()
        {
            Debug.Log("[pb-and-j] spike onChange: '" + (UIInput.current?.value ?? "?") + "'");
        }

        /// <summary>
        /// Removes the game's localization component from a cloned subtree.
        /// </summary>
        /// <remarks>
        /// CILabel.Start assigns label.text from the text library, and
        /// DataManagerText re-applies every registered one on a language change.
        /// A clone runs Start on the next frame — after we have set our text —
        /// so anything we write reverts to the donor's wording and logs a
        /// "failed to resolve localization" warning. The donor input itself
        /// carries none, but its neighbours do, so cloned subtrees get swept.
        /// </remarks>
        private static void StripLocalizers(GameObject root)
        {
            foreach (var label in root.GetComponentsInChildren<CILabel>(true))
            {
                UnityEngine.Object.Destroy(label);
            }
        }

        private static string Describe(UnityEngine.Object? o)
        {
            return o == null ? "(null)" : o.name;
        }

        private static string Describe(UIInput? i)
        {
            return i == null ? "(null)" : i.name;
        }
    }
}
