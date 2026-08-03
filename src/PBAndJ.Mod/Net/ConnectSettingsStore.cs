using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Reads and writes the connect screen's remembered details.
    //
    // Humble object: the format, the parse and the rule that a passphrase is
    // only written on opt-in all live in PBAndJ.Core's ConnectSettings, under
    // the coverage gate. This does the two things a test cannot — touch the
    // filesystem, and ask the game where it keeps its settings.
    [ExcludeFromCodeCoverage]
    internal static class ConnectSettingsStore
    {
        private const string FileName = "pb-and-j.settings.yaml";

        /// <summary>
        /// Where the file lives.
        /// </summary>
        /// <remarks>
        /// Resolved through the game's own path helper rather than composed, so
        /// it works unchanged on Windows and under Proton — the same reason
        /// CombatGameBridge.SaveFolder goes through DataManagerSave.
        /// <para>
        /// The game's settings folder, deliberately, and NOT the mod folder:
        /// `make deploy` does `rm -rf` on the mod folder and recreates it, so
        /// anything kept there is destroyed on every redeploy — which is the one
        /// machine where this gets exercised most. Its neighbours here
        /// (mods.yaml, options.yaml, input.yaml) are all loaded by exact name
        /// and nothing enumerates the folder, so a file of ours cannot disturb
        /// them.
        /// </para>
        /// </remarks>
        private static string? Path_()
        {
            var folder = DataPathHelper.GetSettingsFolder();
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
        }

        internal static ConnectSettings Load()
        {
            try
            {
                var path = Path_();
                if (path == null || !File.Exists(path))
                {
                    return ConnectSettings.Default;
                }

                ConnectSettings.TryParse(File.ReadAllText(path), out var settings);
                return settings;
            }
            catch (Exception e)
            {
                // Never fatal: this is read while the title screen is building,
                // and an unreadable preference must not take the menu with it.
                Debug.Log("[pb-and-j] could not read connect settings: " + e.GetType().Name);
                return ConnectSettings.Default;
            }
        }

        internal static bool Save(ConnectSettings settings)
        {
            var path = Path_();
            if (path == null)
            {
                return false;
            }

            // Staged beside the destination and moved into place, the same
            // discipline CombatGameBridge.WriteScenario uses: an interrupted
            // write must not leave a half-file that parses into nonsense.
            var staging = path + ".pbj-incoming";

            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder!);
                }

                File.WriteAllText(staging, settings.Serialize());

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(staging, path);
                return true;
            }
            catch (Exception e)
            {
                Debug.Log("[pb-and-j] could not save connect settings: " + e.GetType().Name);
                TryDelete(staging);
                return false;
            }
        }

        /// <summary>Forgets everything, including any stored passphrase.</summary>
        internal static bool Forget()
        {
            var path = Path_();
            if (path == null)
            {
                return false;
            }

            // Deleted rather than blanked: a file that still exists invites the
            // question of what is left in it.
            return TryDelete(path);
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
