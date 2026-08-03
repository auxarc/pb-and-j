using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Read-only reconnaissance for the connect screen.
    //
    // THROWAWAY. Delete once its answers are written into
    // docs/notes/ngui-surface.md. It exists because the alternative is guessing:
    // NGUI lives in Assembly-CSharp-firstpass, the game's own view prefabs are
    // serialized data rather than code, and this project has four recorded
    // instances of a plausible reading of the decompile becoming a bug.
    //
    // It answers, for a real running game:
    //   - is CIViewReporter's GameObject inactive at rest? (if it is active, a
    //     clone's Awake hijacks CIViewReporter.ins and breaks the game's own bug
    //     reporter, and cloning it wholesale is off the table)
    //   - does the UIInput's label live inside the subtree worth cloning? Unity
    //     remaps references only within a copied hierarchy, so if it does not, a
    //     cloned field renders into the original's label
    //   - which components carry serialized click delegates that a clone would
    //     drag along
    //   - which widgets carry UILocalize, which silently overwrites a label's
    //     text on every enable
    [ExcludeFromCodeCoverage]
    internal static class UiProbeGlue
    {
        private const int MaxDepth = 12;

        public static string UiDump()
        {
            var report = new StringBuilder();

            report.Append("[pb-and-j] ui-dump\n");
            Dump(report, "CIViewPauseRoot", SafeTransform(() => CIViewPauseRoot.ins));
            Dump(report, "CIViewReporter", SafeTransform(() => CIViewReporter.ins));
            Dump(report, "CIViewDialogConfirmation", SafeTransform(() => CIViewDialogConfirmation.ins));

            // One Debug.Log per line would interleave with the game's own
            // output; the whole tree goes out as one entry so it can be read.
            Debug.Log(report.ToString());
            return "[pb-and-j] ui-dump written to the log";
        }

        private static Transform? SafeTransform(Func<Component?> get)
        {
            try
            {
                var component = get();
                return component == null ? null : component.transform;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Dump(StringBuilder report, string label, Transform? root)
        {
            report.Append("--- ").Append(label).Append(" ---\n");

            if (root == null)
            {
                report.Append("  (not present)\n");
                return;
            }

            report.Append("  root activeSelf=").Append(root.gameObject.activeSelf)
                .Append(" activeInHierarchy=").Append(root.gameObject.activeInHierarchy).Append('\n');

            Walk(report, root, 1);
        }

        private static void Walk(StringBuilder report, Transform node, int depth)
        {
            if (depth > MaxDepth)
            {
                report.Append(Indent(depth)).Append("… (depth limit)\n");
                return;
            }

            report.Append(Indent(depth))
                .Append(node.name)
                .Append(node.gameObject.activeSelf ? "" : " [inactive]")
                .Append(" @").Append(node.localPosition.ToString("F0"));

            AppendComponents(report, node);
            report.Append('\n');

            for (var i = 0; i < node.childCount; i++)
            {
                Walk(report, node.GetChild(i), depth + 1);
            }
        }

        private static void AppendComponents(StringBuilder report, Transform node)
        {
            Component[] components;
            try
            {
                components = node.GetComponents<Component>();
            }
            catch (Exception)
            {
                return;
            }

            var names = new List<string>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    // A missing script still occupies a slot, and is worth
                    // knowing about before cloning the object it sits on.
                    names.Add("<missing>");
                    continue;
                }

                var type = component.GetType();
                var note = DescribeDelegates(component, type);
                names.Add(note == null ? type.Name : type.Name + note);
            }

            if (names.Count > 0)
            {
                report.Append("  [").Append(string.Join(", ", names.ToArray())).Append(']');
            }
        }

        /// <summary>
        /// Reports serialized NGUI event lists and localization keys, which are
        /// the two things a clone silently inherits.
        /// </summary>
        private static string? DescribeDelegates(Component component, Type type)
        {
            try
            {
                if (component is UILocalize localize)
                {
                    // The dangerous one: re-runs on every OnEnable, and adopts
                    // the label's current text as its key when it has none.
                    return "(key='" + localize.key + "')";
                }

                var notes = new List<string>();

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (field.FieldType != typeof(List<EventDelegate>))
                    {
                        continue;
                    }

                    if (!(field.GetValue(component) is List<EventDelegate> list) || list.Count == 0)
                    {
                        continue;
                    }

                    foreach (var entry in list)
                    {
                        if (entry == null)
                        {
                            continue;
                        }

                        var target = entry.target == null ? "?" : entry.target.GetType().Name;
                        notes.Add(field.Name + "->" + target + "." + entry.methodName);
                    }
                }

                return notes.Count == 0 ? null : "(" + string.Join(", ", notes.ToArray()) + ")";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Indent(int depth)
        {
            return new string(' ', depth * 2);
        }
    }
}
