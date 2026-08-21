using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Content.Code.Utility;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PBAndJ.Core.Net;
using PBAndJ.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The multiplayer save catalogue, the lobby built on it, and the campaign
    // bit that decides where saves are written -- the console commands, and the
    // hooks the screen and the actuator call for the same things.
    internal static partial class NetGlue
    {
        // --- the save catalogue (M11b) ---
        //
        // M11a shipped no game console commands at all — it is Core-only, driven
        // from the peer REPL — so these are the first way to work the lobby from
        // inside a running game, and they are how M11b is verifiable before M11c's
        // screen exists.
        //
        // Quantum Console splits arguments on spaces, so a save name with spaces
        // has to be quoted: pbj.save-convert "TWICE SHY" fromsp

        public static string Saves()
        {
            var catalogue = LobbyCatalogue.Multiplayer(SaveCatalogueGlue.List());
            if (catalogue.Count == 0)
            {
                return "[pb-and-j] no multiplayer saves yet — pbj.save-as or pbj.save-convert makes one";
            }

            var text = "[pb-and-j] " + catalogue.Count + " multiplayer save(s), newest first:";
            for (var i = 0; i < catalogue.Count; i++)
            {
                text += "\n  " + catalogue[i].Key;
            }
            return text;
        }

        public static string SaveAs(string name)
        {
            var key = SaveCatalogueGlue.SaveAs(name);
            return key == null
                ? "[pb-and-j] could not save as '" + name + "' — see the log for why"
                : "[pb-and-j] saved the current campaign as " + key;
        }

        public static string SaveConvert(string sourceKey, string name)
        {
            var key = SaveCatalogueGlue.Convert(sourceKey, name);
            return key == null
                ? "[pb-and-j] could not convert '" + sourceKey + "' — see the log for why"
                : "[pb-and-j] copied '" + sourceKey + "' to " + key + " — the original is untouched";
        }

        public static string LobbySelect(string key)
        {
            if (runtime == null)
            {
                return NetLog.NoSession();
            }
            if (!(runtime.Session is HostSession))
            {
                return "[pb-and-j] only the host chooses the lobby's save";
            }

            // The session accepts any key by design — it reads no disk, the same
            // way it reads no clock — so the guard against selecting something that
            // is not there belongs here, at the edge that can actually look.
            if (!LobbyCatalogue.Contains(SaveCatalogueGlue.List(), key))
            {
                return "[pb-and-j] '" + key + "' is not a multiplayer save — pbj.saves lists them";
            }

            runtime.Post(new LocalLobbySelectEvent(key, SaveCatalogueGlue.Digest(key)));
            return "[pb-and-j] lobby save set to " + key;
        }

        // --- the campaign bit (M11d) ---

        /// <summary>
        /// Whether the loaded campaign is a multiplayer one, and which save it is.
        /// </summary>
        /// <remarks>
        /// The only way to see <see cref="MultiplayerCampaign"/> from inside a
        /// running game. It decides where every subsequent save is written, and a
        /// bit that stuck on would prefix a singleplayer campaign's saves — hiding
        /// them from the load screen and from Continue, which reads as the campaign
        /// having been deleted. Worth being able to look at.
        /// </remarks>
        public static string Campaign()
        {
            return MultiplayerCampaign.Active
                ? "[pb-and-j] multiplayer campaign '" + MultiplayerCampaign.SaveKey
                    + "' — saves stay in the " + LobbySaveNames.Prefix + " namespace"
                : "[pb-and-j] not in a multiplayer campaign — saves are written as the game names them";
        }
        /// <summary>
        /// What the lobby screen should draw, or null when there is no session.
        /// </summary>
        /// <remarks>
        /// Composed here rather than in the screen because this is the only place
        /// that holds the runtime, and because host and client are different types
        /// answering the same questions — resolving that once keeps the branch out
        /// of the NGUI code, where no test can reach it. Everything downstream of
        /// this is <see cref="LobbyView"/>, under the gate.
        /// </remarks>
        internal static LobbyView? LobbyView()
        {
            if (runtime == null || killed)
            {
                return null;
            }

            if (runtime.Session is HostSession host)
            {
                return new LobbyView(
                    true, PbjPeerRegistry.HostPeerId, host.Selection.SaveKey, host.LobbyRoster, false);
            }
            if (runtime.Session is ClientSession client)
            {
                return new LobbyView(
                    false, client.PeerId, client.LobbySaveKey, client.LobbyRoster, client.LobbyReadySent);
            }
            return null;
        }

        internal static void PostLocalLobbyReady() => runtime?.Post(new LocalLobbyReadyEvent());

        internal static void PostLocalLobbyUnready() => runtime?.Post(new LocalLobbyUnreadyEvent());

        /// <summary>
        /// Chooses the lobby's save, hashing it on the way. Host only.
        /// </summary>
        internal static bool PostLocalLobbySelect(string key)
        {
            // The same guard pbj.lobby-select applies, and for the same reason:
            // the session accepts any key by design because it reads no disk, so
            // every edge that CAN look has to. Without it, a picker showing a
            // stale grid could hand a singleplayer key to the lobby as its
            // campaign, and every peer would ready onto a save they cannot have.
            if (!LobbyCatalogue.Contains(SaveCatalogueGlue.List(), key))
            {
                Debug.LogWarning("[pb-and-j] refusing to select '" + key + "' — not a multiplayer save");
                return false;
            }

            runtime?.Post(new LocalLobbySelectEvent(key, SaveCatalogueGlue.Digest(key)));
            return true;
        }
    }
}
