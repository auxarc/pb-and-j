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
    /// <summary>
    /// M11b: the multiplayer save catalogue — listing, converting and creating
    /// <c>pbj_</c>-prefixed campaign saves.
    /// </summary>
    /// <remarks>
    /// Humble object, the same split <c>ConnectSettingsStore</c> uses: the naming
    /// rules, the reserved names and what the catalogue may offer all live in
    /// PBAndJ.Core's <see cref="LobbySaveNames"/>, <see cref="LobbySaveRules"/> and
    /// <see cref="LobbyCatalogue"/>, under the coverage gate. This does the two
    /// things a test cannot — touch the filesystem, and ask the game where it keeps
    /// its saves.
    /// <para>
    /// A save's identity is its directory name and nothing else: the metadata's
    /// <c>key</c> field is <c>[YamlIgnore]</c> and is assigned from the folder name
    /// when the game reads it. That is what makes convert a file operation with no
    /// format translation — a multiplayer save <em>is</em> a campaign save.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class SaveCatalogueGlue
    {
        /// <summary>Every save the game can see, prefixed and not.</summary>
        /// <remarks>
        /// Deliberately unfiltered — <see cref="LobbyCatalogue.Multiplayer"/> is
        /// the single place that decides what a lobby may offer, and duplicating
        /// that judgement here is how the two drift apart. Callers that want the
        /// multiplayer view ask Core for it.
        /// <para>
        /// Runs outside the <see cref="SaveVisibilityPatches"/> filter window, so
        /// it sees the truth even though the load screen does not.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<LobbySaveEntry> List()
        {
            var entries = new List<LobbySaveEntry>();
            try
            {
                var headers = DataManagerSave.GetSaveHeaders(true);
                if (headers == null)
                {
                    return entries;
                }

                foreach (var pair in headers)
                {
                    // LoadSaveHeaders hands back a default-constructed metadata for
                    // any directory it could not read, so a null value is not a
                    // case that can happen — but the key is the identity and a
                    // header without one cannot be addressed.
                    if (!string.IsNullOrEmpty(pair.Key))
                    {
                        var time = pair.Value == null ? 0L : pair.Value.timeInSystem;
                        entries.Add(new LobbySaveEntry(pair.Key, time, null));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not list saves: " + e.GetType().Name + ": " + e.Message);
            }
            return entries;
        }

        /// <summary>
        /// Copies <paramref name="sourceKey"/> into a new multiplayer save.
        /// Returns the new key, or null with a reason already logged.
        /// </summary>
        /// <remarks>
        /// <b>A copy, never a move.</b> The original campaign is never touched —
        /// that promise is the whole reason conversion is offered at all.
        /// </remarks>
        internal static string? Convert(string? sourceKey, string? displayName)
        {
            var problem = LobbySaveRules.CheckNewName(displayName, Keys());
            if (problem != LobbySaveProblem.None)
            {
                Debug.LogWarning("[pb-and-j] cannot convert to '" + displayName + "': " + problem);
                return null;
            }

            var source = FolderFor(sourceKey);
            if (source == null || !Directory.Exists(source))
            {
                Debug.LogWarning("[pb-and-j] no save named '" + sourceKey + "' to convert");
                return null;
            }

            var key = LobbySaveNames.KeyFor(displayName);
            var destination = FolderFor(key);
            if (destination == null)
            {
                Debug.LogWarning("[pb-and-j] the game did not report a save folder — cannot convert");
                return null;
            }

            // Staged beside the destination and moved into place, so an interrupted
            // copy cannot leave a half-save behind. That matters more here than it
            // looks: the game's own LoadSaveHeaders gives a header entry to ANY
            // directory in the save folder, even one with no readable metadata, so
            // a partial copy would appear in the catalogue as a real save.
            var staging = destination + ".pbj-incoming";
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);

                foreach (var name in new[]
                         {
                             ScenarioPayload.ContentFileName,
                             ScenarioPayload.MetadataFileName,
                         })
                {
                    var from = Path.Combine(source, name);
                    if (File.Exists(from))
                    {
                        File.Copy(from, Path.Combine(staging, name), true);
                    }
                }

                ClearAutosaveFlags(staging);

                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }
                Directory.Move(staging, destination);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not convert '" + sourceKey + "': "
                    + e.GetType().Name + ": " + e.Message);
                CleanUp(staging);
                return null;
            }

            CopyPreview(sourceKey, key);
            DataManagerSave.GetSaveHeaders(true);
            return key;
        }

        /// <summary>
        /// Writes the campaign currently loaded as a new multiplayer save. Returns
        /// the new key, or null with a reason already logged.
        /// </summary>
        /// <remarks>
        /// Note that <c>DoSave</c> permanently reassigns the static
        /// <c>DataManagerSave.saveName</c>, which <c>GetSavePath</c> and the
        /// temporary-extraction path both read. That is exactly what the game does
        /// when a player saves, so it is not a leak — but do not call this from
        /// anywhere that assumes the previous name survives.
        /// </remarks>
        internal static string? SaveAs(string? displayName)
        {
            var problem = LobbySaveRules.CheckNewName(displayName, Keys());
            if (problem != LobbySaveProblem.None)
            {
                Debug.LogWarning("[pb-and-j] cannot save as '" + displayName + "': " + problem);
                return null;
            }

            if (!DataManagerSave.CanSave(false))
            {
                Debug.LogWarning("[pb-and-j] the game will not save right now — load a campaign first");
                return null;
            }

            var key = LobbySaveNames.KeyFor(displayName);
            try
            {
                DataManagerSave.DoSave(key, SaveLocation.Normal, null, -1, false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not save as '" + key + "': "
                    + e.GetType().Name + ": " + e.Message);
                return null;
            }
            return key;
        }

        /// <summary>
        /// The contents hash of a save, or null if it could not be read.
        /// </summary>
        /// <remarks>
        /// Reuses <see cref="ScenarioPayload"/>'s order-independent FNV-1a so the
        /// lobby's digest and M9's transfer digest are one thing rather than two
        /// that agree by coincidence. A campaign save has the identical two-file
        /// shape a combat scenario does, which is why this works unchanged.
        /// <c>Inspect</c> is deliberately not called — its limits are about what is
        /// sendable, and hashing is not sending.
        /// </remarks>
        internal static string? Digest(string? key)
        {
            try
            {
                var folder = FolderFor(key);
                if (folder == null || !Directory.Exists(folder))
                {
                    return null;
                }

                var files = new List<ScenarioFile>();
                foreach (var name in new[]
                         {
                             ScenarioPayload.ContentFileName,
                             ScenarioPayload.MetadataFileName,
                         })
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path))
                    {
                        files.Add(new ScenarioFile(name, File.ReadAllBytes(path)));
                    }
                }

                return files.Count == 0 ? null : new ScenarioPayload(key, files).Digest;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not hash '" + key + "': "
                    + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Strips the autosave marking from a converted save, through the game's
        /// own serializer.
        /// </summary>
        /// <remarks>
        /// Converting an autosave would otherwise produce a multiplayer save that
        /// is <em>invisible in the picker meant to select it</em>: unlike the save
        /// key, <c>autosaveTextKey</c> IS serialized into <c>metadata.yaml</c>, and
        /// the load grid skips every entry carrying one whenever the player has
        /// turned autosave display off — a toggle they may have flipped hours
        /// earlier and in another screen.
        /// <para>
        /// Round-tripped through <c>UtilitiesYAML</c> rather than edited as text:
        /// the game's own reader and writer make no parser assumptions of ours, and
        /// it happens inside the staging directory before the move regardless.
        /// </para>
        /// </remarks>
        private static void ClearAutosaveFlags(string staging)
        {
            var path = Path.Combine(staging, ScenarioPayload.MetadataFileName);
            if (!File.Exists(path))
            {
                return;
            }

            var metadata = UtilitiesYAML.LoadDataFromFile<DataContainerSavedMetadata>(path, false, false);
            if (metadata == null)
            {
                Debug.LogWarning("[pb-and-j] converted save has unreadable metadata — leaving it as it arrived");
                return;
            }

            metadata.autosaveTextKey = string.Empty;
            metadata.autosaveIndex = -1;
            UtilitiesYAML.SaveDataToFile(path, metadata, false);
        }

        /// <summary>
        /// Copies the save's screenshot alongside it, if there is one.
        /// </summary>
        /// <remarks>
        /// Best-effort and non-fatal: a save with no preview renders without a
        /// screenshot, which is a cosmetic loss and not a broken save. Worth
        /// knowing for M11e — the preview is 47–144 KB against the save's own 62 KB,
        /// so it is <b>larger than the thing it illustrates</b> and has no business
        /// on the wire.
        /// </remarks>
        private static void CopyPreview(string? sourceKey, string destinationKey)
        {
            try
            {
                var root = DataPathHelper.GetSavePreviewFolder();
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }

                var from = Path.Combine(root, sourceKey + ".jpg");
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(root, destinationKey + ".jpg"), true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not copy the save preview: " + e.GetType().Name);
            }
        }

        private static void CleanUp(string staging)
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not clean up '" + staging + "': " + e.GetType().Name);
            }
        }

        private static IEnumerable<string?> Keys()
        {
            var all = List();
            var keys = new List<string?>(all.Count);
            for (var i = 0; i < all.Count; i++)
            {
                keys.Add(all[i].Key);
            }
            return keys;
        }

        /// <summary>
        /// Where a save lives, from the game's own path resolution rather than a
        /// composed path — so this works unchanged on Windows and under Proton,
        /// where the same logical folder lives somewhere quite different.
        /// </summary>
        private static string? FolderFor(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            var root = DataManagerSave.GetSaveFolderPath(SaveLocation.Normal);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, key);
        }
    }
}
