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
    // Tearing the session down when the game quits.

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(Heartbeat), "OnApplicationQuit")]
    internal static class Patch_Heartbeat_OnApplicationQuit
    {
        private static void Postfix()
        {
            NetGlue.Shutdown();
#if PBJ_DRIVE
            DriveGlue.Stop();
#endif
        }
    }
}
