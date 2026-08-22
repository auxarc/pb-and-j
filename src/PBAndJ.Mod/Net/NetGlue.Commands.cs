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
    // Registration with Quantum Console. A [Command] attribute registers nothing
    // in this game, so every command NetGlue owns is added by hand here -- along
    // with a few that live on other glue classes and have no registration of
    // their own.
    internal static partial class NetGlue
    {
        internal static void RegisterConsoleCommands()
        {
            Add(nameof(Host), new Type[0], "pbj.host");
            Add(nameof(Host), new[] { typeof(int) }, "pbj.host");
            Add(nameof(Host), new[] { typeof(string), typeof(int), typeof(string) }, "pbj.host");
            Add(nameof(Join), new[] { typeof(string) }, "pbj.join");
            Add(nameof(Join), new[] { typeof(string), typeof(int) }, "pbj.join");
            Add(nameof(Join), new[] { typeof(string), typeof(int), typeof(string) }, "pbj.join");
            Add(nameof(NetStatus), new Type[0], "pbj.net-status");
            Add(nameof(NetStop), new Type[0], "pbj.net-stop");
            Add(nameof(Ready), new Type[0], "pbj.ready");
            Add(nameof(Unready), new Type[0], "pbj.unready");
            Add(nameof(Rejoin), new Type[0], "pbj.rejoin");
            Add(nameof(ReplayLast), new Type[0], "pbj.replay-last");
            Add(nameof(ScenarioPull), new Type[0], "pbj.scenario-pull");
            Add(nameof(Saves), new Type[0], "pbj.saves");
            Add(nameof(SaveAs), new[] { typeof(string) }, "pbj.save-as");
            Add(nameof(SaveConvert), new[] { typeof(string), typeof(string) }, "pbj.save-convert");
            Add(nameof(LobbySelect), new[] { typeof(string) }, "pbj.lobby-select");
            Add(nameof(Campaign), new Type[0], "pbj.campaign");
            AddFrom(typeof(ConnectScreenGlue), nameof(ConnectScreenGlue.Connect),
                new Type[0], "pbj.connect");
            AddFrom(typeof(ConnectScreenGlue), nameof(ConnectScreenGlue.ConnectForget),
                new Type[0], "pbj.connect-forget");
            AddFrom(typeof(LobbyScreenGlue), nameof(LobbyScreenGlue.Lobby),
                new Type[0], "pbj.lobby");
            AddFrom(typeof(CombatShipGlue), nameof(CombatShipGlue.ShipFight),
                new Type[0], "pbj.ship-fight");
            AddFrom(typeof(CheckpointGlue), nameof(CheckpointGlue.CheckpointStat),
                new Type[0], "pbj.checkpoint-stat");
            // The read side of the same slot, and its own command rather than an
            // optional argument on pbj.combat-load: an argument would leave a bare
            // `pbj.combat-load` still meaning pbj_combat_test while looking as
            // though it might not, and the R0 runbook already names this one.
            AddFrom(typeof(CheckpointGlue), nameof(CheckpointGlue.CheckpointLoad),
                new Type[0], "pbj.checkpoint-load");
        }

        private static void AddFrom(Type owner, string methodName, Type[] parameters, string command)
        {
            var method = owner.GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }

        private static void Add(string methodName, Type[] parameters, string command)
        {
            var method = typeof(NetGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public, null, parameters, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
