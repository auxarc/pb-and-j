using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Scenario transfer (M9): reading the combat save off disk into a payload,
    // writing a received payload back to disk, and resolving the save folder through
    // the game's own DataManagerSave rather than a composed path -- which is what
    // makes this work unchanged under Proton.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        // --- scenario transfer (M9) ---
        //
        // The save directory the game itself writes: SavedGames/<name>/ holding
        // content.zip and metadata.yaml. Resolved through the game's own
        // DataManagerSave.GetSaveFolderPath rather than a composed path, so this
        // works unchanged on Windows and under Proton, where the same logical
        // folder lives somewhere quite different.

        public ScenarioPayload ReadScenario(string? saveKey)
        {
            try
            {
                var folder = SaveFolder(saveKey);
                if (folder == null || !Directory.Exists(folder))
                {
                    return ScenarioPayload.None;
                }

                // Content is split into parts only when it has to be — M11e. Every
                // save measured is far under one part, so the common case still
                // sends a single content.zip exactly as M9 did. Splitting here
                // rather than at the session keeps the wire-size decision in one
                // place: PbjWriter throws on an oversize blob and PbjRuntime.SendTo
                // does not guard encoding, so nothing above may hand it one.
                var files = new List<ScenarioFile>();
                var contentPath = Path.Combine(folder, ScenarioPayload.ContentFileName);
                if (File.Exists(contentPath))
                {
                    files.AddRange(ScenarioPayload.SplitContent(File.ReadAllBytes(contentPath)));
                }

                var metadataPath = Path.Combine(folder, ScenarioPayload.MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    files.Add(new ScenarioFile(
                        ScenarioPayload.MetadataFileName, File.ReadAllBytes(metadataPath)));
                }

                // A partial directory is handed over as-is rather than patched
                // up here: ScenarioPayload.Inspect is the single place that
                // decides what is sendable, and duplicating that judgement in the
                // glue is how the two drift apart.
                return files.Count == 0
                    ? ScenarioPayload.None
                    : new ScenarioPayload(saveKey, files);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not read the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                return ScenarioPayload.None;
            }
        }

        public bool WriteScenario(ScenarioPayload payload)
        {
            // The destination now travels with the payload — M11e. SaveFolder
            // refuses anything outside the namespace, so a forged key fails here
            // rather than composing a path.
            var folder = SaveFolder(payload.SaveName);
            if (folder == null)
            {
                Debug.LogWarning("[pb-and-j] no writable save folder for '"
                    + payload.SaveName + "' — cannot write the save");
                return false;
            }

            // Staged beside the destination and moved into place, so an
            // interrupted or failed write cannot leave a half-save for
            // pbj.combat-load to find and try to enter.
            var staging = folder + ".pbj-incoming";
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);

                // Split content is reassembled here, never written out as parts:
                // the parts are a wire concern and the game must find the ordinary
                // content.zip it wrote. JoinContent orders by part index rather
                // than by arrival, because the digest is order-independent and
                // nothing promises the wire preserved file order.
                for (var i = 0; i < payload.Files.Count; i++)
                {
                    var file = payload.Files[i];
                    // Belt and braces. The session already refused anything that
                    // is not allowlisted, but this is the statement that actually
                    // composes a path, so it is the one that has to be safe on
                    // its own terms.
                    if (!ScenarioPayload.IsAllowedName(file.Name))
                    {
                        Debug.LogWarning("[pb-and-j] refusing to write scenario file '"
                            + file.Name + "' — not an allowed name");
                        Directory.Delete(staging, true);
                        return false;
                    }
                    if (ScenarioPayload.PartIndex(file.Name) >= 0)
                    {
                        continue;
                    }
                    if (string.Equals(file.Name, ScenarioPayload.ContentFileName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    File.WriteAllBytes(Path.Combine(staging, file.Name), file.Content);
                }

                File.WriteAllBytes(
                    Path.Combine(staging, ScenarioPayload.ContentFileName),
                    ScenarioPayload.JoinContent(payload));

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
                Directory.Move(staging, folder);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not write the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }
                catch (Exception cleanup)
                {
                    Debug.LogWarning("[pb-and-j] could not clean up '" + staging + "': "
                        + cleanup.GetType().Name);
                }
                return false;
            }
        }

        /// <summary>
        /// Where a save lives, from the game's own path resolution. The
        /// directory name is always ours — never the one on the wire.
        /// </summary>
        /// <remarks>
        /// <b>The one statement in the mod that turns a wire-supplied name into a
        /// path</b>, so the guard is here and not only at the caller. M9 passed a
        /// constant and needed no check; M11e carries the lobby's key, and
        /// <see cref="ScenarioPayload.IsAllowedDestination"/> is what stands between
        /// that and a <c>Path.Combine</c>. Refusing here rather than trusting the
        /// session keeps this safe on its own terms — the session checking first is
        /// defence in depth, not a substitute.
        /// </remarks>
        private static string? SaveFolder(string? saveKey)
        {
            if (!ScenarioPayload.IsAllowedDestination(saveKey))
            {
                Debug.LogWarning("[pb-and-j] refusing to resolve a save folder for '"
                    + saveKey + "' — not an allowed destination");
                return null;
            }

            var root = DataManagerSave.GetSaveFolderPath(SaveLocation.Normal);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, saveKey);
        }
    }
}
