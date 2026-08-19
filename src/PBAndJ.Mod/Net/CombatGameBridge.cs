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
    // Humble-object glue: the entire ECS surface Core needs, expressed without
    // Core ever seeing a game type. No logic lives here beyond field copying
    // and the guards the game itself requires.
    //
    // This class is split across twelve files. This one holds the class prose,
    // the interface declaration and the three ECS state queries; everything else
    // is a sibling, and InCombat here is the guard eight of those members share:
    //
    //   .Turn.cs        the execute lock, the orders, the commit
    //   .Snapshot.cs    reading unit state OUT of the ECS, and the digest
    //   .Snapshot.Apply.cs   writing a received snapshot back INTO it
    //   .Keyframes.cs   the end-of-turn capture a host ships, and its slicing
    //   .PoseTracks.cs  one unit's poses, reaction pings and melee swings
    //   .Assets.cs      the replay asset tracks -- beams, trails
    //   .Vec.cs         the four Unity-to-wire converters
    //   .Playback.cs    the client end: play what the host sent, and stop
    //   .Overworld.cs   MirrorBase, the only non-combat member of the class
    //   .Handoff.cs     the three one-line save/load delegations
    //   .Scenario.cs    scenario transfer (M9) and the save folder
    //
    [ExcludeFromCodeCoverage]
    internal sealed partial class CombatGameBridge : IPbjGameBridge
    {
        public int CurrentTurn
        {
            get
            {
                var combat = Contexts.sharedInstance.combat;
                // currentTurn throws when the component is absent, which it is
                // outside combat.
                return combat.hasCurrentTurn ? combat.currentTurn.i : -1;
            }
        }

        public bool InCombat =>
            IDUtility.IsGameState("combat") && Contexts.sharedInstance.combat.hasCurrentTurn;

        public IReadOnlyList<string> AssignableUnitNames
        {
            get
            {
                var names = new List<string>();
                if (!InCombat)
                {
                    return names;
                }
                foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
                {
                    // Player-controllable AND friendly: friendly alone would
                    // include scenario-scripted AI allies, whose orders would
                    // fight the AI planning systems.
                    if (!unit.isPlayerControllable || !CombatUIUtility.IsUnitFriendly(unit))
                    {
                        continue;
                    }
                    var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                    if (persistent != null && persistent.hasNameInternal)
                    {
                        names.Add(persistent.nameInternal.s);
                    }
                }
                return names;
            }
        }

    }
}
