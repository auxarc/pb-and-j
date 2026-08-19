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
    // Handing a save between machines. Three one-line delegations, and the name is
    // Handoff rather than Load because only two of them load: BeginLoad takes a
    // campaign save (M11d), BeginCombatLoad takes the fight a host shipped (M12b),
    // and ShipCombat goes the other way -- it arms the write that PRODUCES the save
    // a host offers.
    //
    // ShipCombat only arms it; the save itself is CombatShipGlue's, which polls
    // until the game will accept one. All three bodies are a single call.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        /// <summary>
        /// Loads the fight the host shipped. M12b.
        /// </summary>
        /// <remarks>
        /// Routed to <see cref="LoadGlue.BeginCombat"/> rather than
        /// <see cref="LoadGlue.Begin"/>, and the difference is not cosmetic: the
        /// campaign path checks the lobby catalogue, which deliberately excludes
        /// the scenario slot, so a fight sent through it returns Unavailable
        /// every single time and reads as a missing save rather than as wiring.
        /// </remarks>
        public LoadOutcome? BeginCombatLoad(string? saveName, string? digest)
        {
            return LoadGlue.BeginCombat(saveName, digest);
        }

        /// <summary>
        /// Writes the fight we have just entered, so it can be offered. M12b.
        /// </summary>
        /// <remarks>
        /// Only arms the write. The game refuses to save while the scenario intro
        /// runs, and raises that flag in the same tick that makes
        /// <see cref="InCombat"/> true, so <see cref="CombatShipGlue"/> polls from
        /// the next frame on and answers with <c>LocalCombatReadyEvent</c> when it
        /// has a save — or when it has given up on getting one.
        /// </remarks>
        public void ShipCombat()
        {
            CombatShipGlue.Arm();
        }

        /// <summary>
        /// Starts loading a campaign save. M11d.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="LoadGlue"/>, which owns the pre-checks and the
        /// completion callback. Kept out of this class because the bridge is
        /// otherwise all ECS reads and writes, and a load is neither — it tears
        /// the ECS down and builds a new one.
        /// </remarks>
        public LoadOutcome? BeginLoad(string? saveKey, int selectionVersion, string? saveDigest) =>
            LoadGlue.Begin(saveKey, selectionVersion, saveDigest);
    }
}
