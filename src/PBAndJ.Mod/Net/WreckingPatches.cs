using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat.Systems;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M17 stage 2. Lets a client set <c>persistent.isWrecked</c> without paying
    /// for the two cascades the flag normally wakes, and stops the client
    /// deciding the fight is over.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The mechanism is an all-false <c>Filter</c>, which is a real
    /// off-switch and not a guess.</b> <c>Entitas.ReactiveSystem.Execute</c>
    /// returns before the abstract <c>Execute(List)</c> when nothing survives
    /// the filter, and <c>Collector.ClearCollectedEntities</c> releases every
    /// retain before clearing — so a suppressed system leaks nothing and leaves
    /// no partial list. Checked in <c>vendor/Managed/Entitas.dll</c>, decompiled
    /// in place.
    /// <para>
    /// ⚠️ <b><c>Filter</c> is <c>protected</c></b>, so the attribute must use the
    /// string-name form. <c>nameof</c> cannot see it. Each of the two target
    /// types declares exactly one <c>Filter</c>, so there is no overload to
    /// disambiguate.
    /// </para>
    /// <para>
    /// 🔴 <b>Nothing in the build can tell you these applied.</b>
    /// <c>src/PBAndJ.Mod</c> is in <c>UNCOVERED_PROJECTS</c>, so a patch whose
    /// target moved compiles, deploys, never runs, and no oracle notices. That
    /// is what <see cref="CascadeFiltered"/> / <see cref="CascadePassed"/> and
    /// <c>pbj.wreck-patches</c> exist for: a counter that can read zero, and a
    /// resolution check that names its own alternative hypothesis.
    /// </para>
    /// <para>
    /// <b>Two systems are deliberately NOT patched.</b>
    /// <c>CombatUnitWreckingSyncSystem</c> is left alone because its whole
    /// <c>Execute</c> is a crash-overlay refresh a client wants, and because its
    /// trigger is <c>Wrecked.AddedOrRemoved()</c> — it is what makes un-wrecking
    /// work, where the two suppressed systems collect only <c>.Added()</c>.
    /// <c>CombatPartWreckingSystem</c> is left alone because it triggers on the
    /// <b>equipment</b> <c>Wrecked</c> flag, which no client path sets; a patch
    /// there would be silently dead.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class WreckingPatches
    {
        /// <summary>
        /// Filter calls this run has forced to false. Read beside
        /// <see cref="CascadePassed"/>, never alone.
        /// </summary>
        /// <remarks>
        /// 🔴 <b>The two counters answer different questions and a zero means
        /// nothing on its own.</b> <c>filtered=0 passed=0</c> is "the Filter was
        /// never called at all" — either the patch did not apply or nothing was
        /// wrecked. <c>filtered=0 passed&gt;0</c> is "the patch applied and the
        /// predicate was false", i.e. no live client session. Only the second is
        /// a fact about the session.
        /// </remarks>
        internal static int CascadeFiltered { get; private set; }

        /// <summary>Filter calls this run has left alone. See the pair above.</summary>
        internal static int CascadePassed { get; private set; }

        /// <summary>
        /// Set for the duration of one deliberate <c>pbj.force-end</c>, so the
        /// rig keeps an escape hatch the accidental routes do not get.
        /// </summary>
        internal static bool BypassCombatEndOnce { get; set; }

        /// <summary>
        /// True only while a live client session owns this fight's outcome.
        /// </summary>
        /// <remarks>
        /// Evaluated per call and never at patch time: the patch is static and
        /// applies to a host's instance of the same system too, so the decision
        /// has to be about the session that exists right now.
        /// <para>
        /// 🔴 The state test is <see cref="ClientSession.ClientOwnsCombatOutcome"/>
        /// in Core rather than an <c>if</c> here, and that is the whole reason it
        /// is a Core member: <c>Closed</c> and <c>Faulted</c> must be excluded or
        /// a post-fault fight — which the human keeps playing single-player —
        /// becomes unwinnable and unlosable for ever, and nothing in this
        /// assembly is covered well enough to catch getting it wrong.
        /// </para>
        /// </remarks>
        internal static bool SuppressCombatEnd
        {
            get
            {
                if (BypassCombatEndOnce)
                {
                    return false;
                }
                return NetGlue.Session is ClientSession client
                    && ClientSession.ClientOwnsCombatOutcome(client.State);
            }
        }

        /// <summary>
        /// True while the two damaging wreck cascades must be suppressed.
        /// </summary>
        /// <remarks>
        /// The same predicate as <see cref="SuppressCombatEnd"/> today, kept
        /// separate because they are separate decisions: one is about who may end
        /// a fight, the other about who may run damage resolution's aftermath.
        /// A future milestone moving one must not silently move the other.
        /// <para>
        /// Deliberately does NOT consult <see cref="BypassCombatEndOnce"/> — that
        /// hatch is about ending combat, and a wreck cascade fired during it
        /// would still create thirty frozen fragments per corpse.
        /// </para>
        /// </remarks>
        internal static bool SuppressCascade =>
            NetGlue.Session is ClientSession client
            && ClientSession.ClientOwnsCombatOutcome(client.State);

        /// <summary>
        /// Narrows a <c>Filter</c> result and counts which way it went.
        /// </summary>
        internal static void CountFilter(ref bool result)
        {
            if (result && SuppressCascade)
            {
                result = false;
                CascadeFiltered++;
                return;
            }
            if (result)
            {
                CascadePassed++;
            }
        }

        /// <summary>Forgets the counters, for a session or a fight that ended.</summary>
        internal static void ResetCounters()
        {
            CascadeFiltered = 0;
            CascadePassed = 0;
        }
    }

    // The unit-level wreck cascade, suppressed on a client.
    //
    // A POSTFIX, not a prefix: a prefix that skipped the original would have to
    // invent the host's answer, while a postfix only ever narrows a true to a
    // false, which is exactly the semantic wanted. `ref bool __result` because
    // the original is `protected override bool Filter`.
    //
    // What the suppression buys, out of CombatUnitWreckingSystem.Execute:
    // DestroyAllActions on a corpse whose action set is the host's to decide; a
    // locally invented ReplaceUnitFrameDefects, which is SERIALIZED and which
    // vanilla reads to decide whether a unit is destroyed for good; a modal
    // pause dialog raised mid-playback on a UI with no view stack; a CrumpleTime
    // whose only remover triggers on Simulating.Removed() and so would be
    // permanent litter here; and arbitrary content effect functions on a machine
    // that is replaying rather than simulating.
    //
    // Two lines of that cascade ARE wanted and are repaid by hand:
    // visualManager.OnUnitDestruction() (ours since M15) and the
    // AddScenarioStateRefreshContext(OnUnitDisabled) poke, in
    // KeyframePlayer.DriveWreckFlag.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatUnitWreckingSystem), "Filter")]
    internal static class Patch_CombatUnitWreckingSystem_Filter
    {
        private static void Postfix(ref bool __result)
        {
            WreckingPatches.CountFilter(ref __result);
        }
    }

    // The debris cascade, suppressed on a client for a blunter reason: its
    // Execute creates up to thirty real CombatEntity projectiles per wreck, with
    // TimeToLive, SimpleMovement and SimpleForce -- and every system that moves
    // or expires them is simulation-gated. On a client that is thirty frozen
    // fragments at the corpse's core, per wreck, for the rest of the fight.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatUnitDestructionEffectSystem), "Filter")]
    internal static class Patch_CombatUnitDestructionEffectSystem_Filter
    {
        private static void Postfix(ref bool __result)
        {
            WreckingPatches.CountFilter(ref __result);
        }
    }

    // The sole chokepoint for ending a fight. ReplaceCombatResolved has exactly
    // one caller in the whole game and it is inside this method.
    //
    // 🔴 REQUIRED, not defence in depth. A client CAN reach the victory count:
    // the bit that gates it accumulates by bitwise OR and has a second,
    // content-driven producer in CombatScenarioTransitionSystem, and the
    // OnUnitDisabled poke this milestone repays by hand is one of the two
    // contexts that fills the accumulator. The poke and this prefix ship
    // together or not at all.
    //
    // `nameof` is correct here and NOT in the two above: EndCombatWithOutcome is
    // public static, where Filter is protected.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(ScenarioUtility), nameof(ScenarioUtility.EndCombatWithOutcome))]
    internal static class Patch_ScenarioUtility_EndCombatWithOutcome
    {
        private static bool Prefix()
        {
            return !WreckingPatches.SuppressCombatEnd;
        }
    }
}
