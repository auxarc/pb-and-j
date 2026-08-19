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
    // The four Unity-to-wire converters, and nothing else.
    //
    // They are apart from all three keyframe parts deliberately, and the numbers are
    // closer than that sounds: of 27 call sites (ToVec3 17, ToVec4 10), 19 are in
    // Assets.cs and the other 8 split evenly between Keyframes.cs and PoseTracks.cs.
    // So Assets.cs has a plurality, not ownership -- folding them in would give that
    // file a header covering less than its contents while two other parts reached
    // into it. A file whose name covers exactly what it holds is worth its 43 lines.
    //
    // KeyframePlayer, the class that undoes what these do, makes the opposite choice
    // -- its four wire-to-Unity converters sit in the primary KeyframePlayer.cs
    // (:214-220). That is not the precedent it looks like: every caller of its
    // ToVector3 is in KeyframePlayer.Assets.cs, so one part there really does own
    // them, and a review has already flagged the placement. These are shared for
    // real, which is why they are filed apart instead.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        private static Vec3 ToVec3(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static Vec4 ToVec4(Quaternion q) => new Vec4(q.x, q.y, q.z, q.w);

        private static Vec4 ToVec4(Vector4 v) => new Vec4(v.x, v.y, v.z, v.w);

        private static Vec4 ToVec4(Color c) => new Vec4(c.r, c.g, c.b, c.a);
    }
}
