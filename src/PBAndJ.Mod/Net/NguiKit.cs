using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// The NGUI cloning primitives the mod's screens are built from.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="ConnectScreenGlue"/> when the lobby screen needed
    /// the same pieces. It is deliberately one implementation rather than two: the
    /// value here is not the code, it is the half-dozen traps encoded in it, each
    /// of which cost a build to find. A second copy would drift from the first and
    /// re-pay for them.
    /// <para>
    /// Everything relies on <c>CIViewReporter</c> being <b>inactive at rest</b>, so
    /// <c>Instantiate</c> does not run <c>Awake</c>, clones can be configured
    /// before they wake, and <c>CIViewReporter.ins</c> is never hijacked. That was
    /// observed on a running game, not inferred — <c>docs/notes/ngui-surface.md</c>
    /// is the record, and now the only one, since the probes that produced it have
    /// been deleted.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class NguiKit
    {
        /// <summary>
        /// Copies a donor GameObject into a screen root at a local offset.
        /// </summary>
        internal static GameObject? Clone(GameObject? root, GameObject? donor, float x, float y)
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
        /// <c>CILabel.Start</c> assigns <c>label.text</c> from the text library, and
        /// <c>DataManagerText</c> re-applies every registered one on a language
        /// change, so anything we write would revert to the donor's wording — on
        /// the frame after activation, which is the worst kind of bug to chase.
        /// <c>DestroyImmediate</c> rather than <c>Destroy</c> because <c>Destroy</c>
        /// is deferred to the end of the frame, and the root may be activated
        /// before then.
        /// </remarks>
        internal static void StripLocalizers(GameObject subtree)
        {
            foreach (var label in subtree.GetComponentsInChildren<CILabel>(true))
            {
                UnityEngine.Object.DestroyImmediate(label);
            }
        }

        /// <summary>A caption or a line of text.</summary>
        /// <remarks>
        /// The only <c>UILabel</c> <c>CIViewReporter</c> exposes directly is the one
        /// its <c>UIInput</c> renders through — it is registered as the "input" nav
        /// node. A clone therefore arrives carrying a <c>UIInput</c>, whose
        /// <c>UpdateLabel</c> overwrites the text with the donor's placeholder the
        /// moment the screen is activated. That is what once put "Default feedback
        /// text. You can click here to edit the message." on every caption.
        /// <para>
        /// Stripped by component rather than by picking a different donor, so this
        /// stays right whichever label the field happens to point at.
        /// </para>
        /// </remarks>
        internal static UILabel? CloneLabel(
            GameObject? root, UILabel? donor, string text, float x, float y, int width)
        {
            var clone = Clone(root, donor == null ? null : donor.gameObject, x, y);
            if (clone == null)
            {
                return null;
            }

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
                label.width = width;
                label.multiLine = true;
                label.overflowMethod = UILabel.Overflow.ResizeHeight;
            }

            return label;
        }

        internal static CIButton? CloneButton(
            GameObject? root, CIButton? donor, string text, float x, float y, Action onClick)
        {
            var clone = Clone(root, donor == null ? null : donor.gameObject, x, y);
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

        /// <summary>A dark panel backing, so text is readable over the menu art.</summary>
        internal static UIWidget? CloneBackground(
            GameObject? root, CIViewReporter reporter, int width, int height, float pad)
        {
            var donor = FindBackgroundDonor(reporter);
            var clone = Clone(root, donor == null ? null : donor.gameObject, -pad, pad);
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
                widget.width = width;
                widget.height = height;

                // Near-opaque, not merely dark. The title menu render swings from
                // a dim skyline to a full-screen explosion, and at 0.88 the panel
                // washed out to light grey on the bright frames. Cool rather than
                // neutral so it reads as part of the UI instead of a hole punched
                // in it — the game's own menu strip solves this the same way.
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
        internal static Transform? FindBackgroundDonor(CIViewReporter reporter)
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
        /// Pushes the panel backing behind everything, and any midground pieces
        /// (field boxes) between it and the text.
        /// </summary>
        internal static void SendToBack(
            GameObject? root, UIWidget? background, IList<UIWidget>? midground)
        {
            if (root == null)
            {
                return;
            }

            var lowest = int.MaxValue;
            foreach (var widget in root.GetComponentsInChildren<UIWidget>(true))
            {
                if (widget == background || (midground != null && midground.Contains(widget)))
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

            if (midground != null)
            {
                foreach (var piece in midground)
                {
                    piece.depth = lowest - 1;
                }
            }

            if (background != null)
            {
                background.depth = lowest - 2;
            }
        }

        /// <summary>
        /// Shows or hides a button.
        /// </summary>
        /// <remarks>
        /// Hiding rather than <c>CIButton.available = false</c>, which was the
        /// obvious lever and is not what shipped: its disabled-click path reads
        /// <c>audio.enabled</c> with no null check, and a clone's audio block is
        /// whatever the donor happened to serialize. <c>SetActive(false)</c> never
        /// reaches that code.
        /// <para>
        /// <c>ForceHoverEvent</c> first because deactivating a hovered button
        /// strands its tooltip on screen — the case <c>SetAvailableInstantly</c>
        /// handles explicitly and plain deactivation does not.
        /// </para>
        /// </remarks>
        internal static void Show(CIButton? button, bool visible)
        {
            if (button == null || button.gameObject.activeSelf == visible)
            {
                return;
            }

            if (!visible)
            {
                button.ForceHoverEvent(isHovered: false, conservative: true);
            }

            button.gameObject.SetActive(visible);
        }
    }
}
