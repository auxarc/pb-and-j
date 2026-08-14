using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Entitas;
using HarmonyLib;
using PhantomBrigade;
using PhantomBrigade.Combat.Systems;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // THROWAWAY, like the four probes deleted with M10. This one exists to
    // answer the questions docs/notes/replay-handoff-recon.md ends on, before
    // M8 is designed around guesses about them. Delete it once the answers are
    // written into that file.
    //
    // Every question here was reached by reading the decompile, and this
    // project has now paid five times for the difference between a careful
    // reading and a running game. Six sections, each independently guarded, so
    // one unavailable API costs its own answer and not the others.
    //
    // Console return values do NOT reach Player.log — Quantum Console renders
    // them in its own view — so everything worth keeping is Debug.Log'd.
    [ExcludeFromCodeCoverage]
    internal static class ReplayProbeGlue
    {
        private const string Tag = "[pb-and-j] replay-probe";

        public static string ReplayProbe()
        {
            var report = new StringBuilder();
            report.Append(Tag).Append('\n');

            Section(report, "time", ProbeTime);
            Section(report, "animation flags", ProbeAnimationFlags);
            Section(report, "replay statics", ProbeReplayStatics);
            Section(report, "apply-time singletons", ProbeSingletons);
            Section(report, "bone identity", ProbeBones);
            Section(report, "track volume", ProbeTracks);

            Debug.Log(report.ToString());
            return Tag + " written to the log";
        }

        /// <remarks>
        /// One section failing must not cost the others. A probe that answers
        /// four of six questions is worth a build; one that throws on the first
        /// missing API is worth nothing.
        /// </remarks>
        private static void Section(StringBuilder report, string name, Action<StringBuilder> body)
        {
            report.Append("--- ").Append(name).Append(" ---\n");
            try
            {
                body(report);
            }
            catch (Exception e)
            {
                report.Append("  FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }
        }

        // --- Q1 continued: the SAME question, but sampled during playback ---

        // ProbeTime below answers "what is timeScale when I ask", which is not
        // the question. The question is what it is *during playback*, on a
        // client, and that window is about five seconds long — far too short to
        // hit by hand over the drive channel, and hitting it by luck would be a
        // sample of one with no way to tell a miss from a zero.
        //
        // So sample from the pump instead, gated on KeyframePlayer.IsPlaying,
        // which is precisely the window in question. No console command arms
        // this: an arming step is one more thing to forget, and the cost of
        // always-on is a handful of float compares per frame during playback.
        //
        // Reports min/max rather than a single reading because the hazard is
        // "was it EVER above zero" — one non-zero frame is enough for
        // UpdateAnimationsForAll to run manual IK solves against our bone
        // writes, and a mean would hide it.
        private static bool wasPlaying;
        private static int sampleFrames;
        private static float scaleMin;
        private static float scaleMax;
        private static int framesAboveZero;

        /// <summary>
        /// Pumped from the Heartbeat postfix, immediately after
        /// <c>KeyframePlayer.Advance</c>, so the sampled window is exactly the
        /// one playback ran in.
        /// </summary>
        internal static void SampleDuringPlayback()
        {
            var playing = KeyframePlayer.IsPlaying;

            if (playing && !wasPlaying)
            {
                sampleFrames = 0;
                framesAboveZero = 0;
                scaleMin = float.MaxValue;
                scaleMax = float.MinValue;
            }

            if (playing)
            {
                var scale = Time.timeScale;
                sampleFrames++;
                if (scale > 0f)
                {
                    framesAboveZero++;
                }
                if (scale < scaleMin)
                {
                    scaleMin = scale;
                }
                if (scale > scaleMax)
                {
                    scaleMax = scale;
                }
            }

            if (!playing && wasPlaying)
            {
                ReportPlaybackWindow();
            }

            wasPlaying = playing;
        }

        private static void ReportPlaybackWindow()
        {
            if (sampleFrames == 0)
            {
                return;
            }

            var simulating = Contexts.sharedInstance.combat.Simulating;

            // The derived verdict, not just the numbers. framesAboveZero > 0
            // with Simulating false is exactly the condition under which
            // MechAnimationSystem's non-reactive Execute calls
            // UpdateAnimationsForAll, which calls LateUpdateUnit directly, which
            // runs the manual FinalIK solves that would fight M8's bone writes.
            var hazardLive = framesAboveZero > 0 && !simulating;

            Debug.Log(Tag + " playback window | frames=" + sampleFrames
                + " timeScale min=" + F(scaleMin) + " max=" + F(scaleMax)
                + " framesAboveZero=" + framesAboveZero
                + " simulating=" + simulating
                + " session=" + (NetGlue.HasSession ? (NetGlue.IsHost ? "host" : "CLIENT") : "none")
                + " | FinalIK hazard " + (hazardLive ? "LIVE — pauseUpdates is load-bearing"
                                                     : "dormant — pauseUpdates is insurance"));
        }

        // --- Q1: a client's Time.timeScale, never once observed ---

        /// <remarks>
        /// The whole FinalIK hazard hangs on this. MechAnimationSystem's
        /// non-reactive Execute calls UpdateAnimationsForAll every frame when
        /// !Simulating && timeScale > 0, and that calls LateUpdateUnit directly
        /// — so on a client, where Simulating is always false, timeScale alone
        /// decides whether manual IK solves fight replayed bone writes.
        /// </remarks>
        private static void ProbeTime(StringBuilder report)
        {
            report.Append("  Time.timeScale=").Append(F(Time.timeScale))
                .Append(" deltaTime=").Append(F(Time.deltaTime))
                .Append(" unscaledDeltaTime=").Append(F(Time.unscaledDeltaTime)).Append('\n');

            var combat = Contexts.sharedInstance.combat;
            report.Append("  Simulating=").Append(combat.Simulating)
                .Append(" hasSimulationTime=").Append(combat.hasSimulationTime)
                .Append(" simulationTime=").Append(combat.hasSimulationTime ? F(combat.simulationTime.f) : "-")
                .Append('\n');
            report.Append("  hasSimulationTimeScale=").Append(combat.hasSimulationTimeScale)
                .Append(" simulationTimeScale=").Append(combat.hasSimulationTimeScale ? F(combat.simulationTimeScale.f) : "-")
                .Append(" hasSimulationDeltaTime=").Append(combat.hasSimulationDeltaTime)
                .Append('\n');
            report.Append("  hasCurrentTurn=").Append(combat.hasCurrentTurn)
                .Append(" currentTurn=").Append(combat.hasCurrentTurn ? combat.currentTurn.i.ToString() : "-")
                .Append(" hasTurnLength=").Append(combat.hasTurnLength)
                .Append(" turnLength=").Append(combat.hasTurnLength ? combat.turnLength.i.ToString() : "-")
                .Append('\n');
        }

        private static void ProbeAnimationFlags(StringBuilder report)
        {
            report.Append("  lateExecuteUnconditional=").Append(MechAnimationSystem.lateExecuteUnconditional)
                .Append(" lateExecuteRequested=").Append(MechAnimationSystem.lateExecuteRequested)
                .Append(" animatorUpdateManual=").Append(MechAnimationSystem.animatorUpdateManual)
                .Append('\n');
            report.Append("  areAnimatorsUpdatedInMain=").Append(CombatReplayHelper.areAnimatorsUpdatedInMain)
                .Append(" areAnimatorsUpdatedInLate=").Append(CombatReplayHelper.areAnimatorsUpdatedInLate)
                .Append('\n');
        }

        // --- Q2: does a client have to seed previewTimeLimit / turnStartTime? ---

        /// <remarks>
        /// ApplyTime clamps every request to previewTimeLimit, which defaults
        /// to 5 and is otherwise assigned only in OnExecutionEnd; turnStartTime
        /// is assigned only in OnExecutionStart. Neither runs on a client, so
        /// the defaults are what playback would actually get.
        /// <para>
        /// activationAllowed is private and is the one condition of
        /// IsReplayAllowed that a client cannot satisfy — reported separately
        /// from the verdict so the reason is visible rather than inferred.
        /// </para>
        /// </remarks>
        private static void ProbeReplayStatics(StringBuilder report)
        {
            report.Append("  activeLast=").Append(CombatReplayHelper.activeLast)
                .Append(" playbackActive=").Append(CombatReplayHelper.playbackActive)
                .Append(" experimentalMode=").Append(CombatReplayHelper.experimentalMode)
                .Append(" experimentalUpdate=").Append(CombatReplayHelper.experimentalUpdate)
                .Append('\n');
            report.Append("  previewTime=").Append(F(CombatReplayHelper.previewTime))
                .Append(" turnStartTime=").Append(F(CombatReplayHelper.turnStartTime))
                .Append(" previewTimeLimit=").Append(F(CombatReplayHelper.previewTimeLimit))
                .Append('\n');
            report.Append("  units.Count=").Append(CombatReplayHelper.units?.Count ?? -1)
                .Append(" IsRecordingAllowed=").Append(CombatReplayHelper.IsRecordingAllowed())
                .Append(" IsReplayAllowed=").Append(CombatReplayHelper.IsReplayAllowed())
                .Append('\n');

            report.Append("  activationAllowed=").Append(PrivateStatic<bool>("activationAllowed"))
                .Append(" recordingAllowed=").Append(PrivateStatic<bool>("recordingAllowed"))
                .Append(" ins=").Append(PrivateStaticObject("ins") == null ? "NULL" : "present")
                .Append('\n');

            var input = Contexts.sharedInstance.input;
            report.Append("  combatUIMode=")
                .Append(input.hasCombatUIMode ? input.combatUIMode.e.ToString() : "(none)")
                .Append('\n');
        }

        private static string PrivateStatic<T>(string field)
        {
            var info = AccessTools.Field(typeof(CombatReplayHelper), field);
            return info == null ? "(no field)" : info.GetValue(null).ToString();
        }

        private static object? PrivateStaticObject(string field)
        {
            var info = AccessTools.Field(typeof(CombatReplayHelper), field);
            return info?.GetValue(null);
        }

        // --- Q3: does ApplyTime throw on a null singleton outside replay mode? ---

        private static void ProbeSingletons(StringBuilder report)
        {
            report.Append("  timeline=").Append(Null(CIViewCombatTimeline.ins))
                .Append(" execution=").Append(Null(CIViewCombatExecution.ins))
                .Append(" strike=").Append(Null(CombatStrikeHelper.ins))
                .Append(" scene=").Append(Null(CombatSceneHelper.ins))
                .Append(" timeControl=").Append(Null(CIViewCombatTimeControl.ins))
                .Append(" postprocessing=").Append(Null(PostprocessingHelper.ins))
                .Append('\n');
        }

        private static string Null(UnityEngine.Object? o) => o == null ? "NULL" : "ok";

        // --- Q4: are mech bone names unique, and do the two sides agree? ---

        /// <remarks>
        /// The recorded bone list is what pose keyframes index into,
        /// positionally and — on the playback path in ApplyTimeToUnit — with no
        /// length guard whatsoever. Names are the proposed identity key, so
        /// duplicates would sink the scheme before it is written.
        /// <para>
        /// The concrete visual manager type is reported per unit because mechs
        /// and tanks use different ones and compose the list entirely
        /// differently; the design doc analysed the tank class by mistake.
        /// </para>
        /// </remarks>
        private static void ProbeBones(StringBuilder report)
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasSimulationTime)
            {
                report.Append("  not in combat\n");
                return;
            }

            var shown = 0;
            foreach (var unit in combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                var name = persistent != null && persistent.hasNameInternal ? persistent.nameInternal.s : "?";

                if (!unit.hasCombatView || unit.combatView.view == null)
                {
                    report.Append("  ").Append(name).Append(": no combat view\n");
                    continue;
                }

                var manager = unit.combatView.view.visualManager;
                if (manager == null)
                {
                    report.Append("  ").Append(name).Append(": no visual manager\n");
                    continue;
                }

                var bones = manager.GetRecordedBones();
                report.Append("  ").Append(name)
                    .Append(" | ").Append(manager.GetType().Name)
                    .Append(" | mechView=").Append(unit.hasMechAnimationView)
                    .Append(" tankView=").Append(unit.hasTankAnimationView)
                    .Append(" | bones=").Append(bones == null ? -1 : bones.Count);

                if (bones != null)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var duplicates = 0;
                    var nulls = 0;
                    foreach (var bone in bones)
                    {
                        if (bone == null)
                        {
                            nulls++;
                            continue;
                        }
                        if (!seen.Add(bone.name))
                        {
                            duplicates++;
                        }
                    }
                    report.Append(" distinct=").Append(seen.Count)
                        .Append(" duplicates=").Append(duplicates)
                        .Append(" nulls=").Append(nulls);
                }
                report.Append('\n');

                // The full name list, once, so the ordering can be compared
                // against a second machine's by eye. Every unit would flood the
                // log for nothing — equipment differences are what matter and
                // one sample shows the shape.
                if (shown == 0 && bones != null)
                {
                    report.Append("    names: ");
                    for (var i = 0; i < bones.Count; i++)
                    {
                        if (i > 0)
                        {
                            report.Append(", ");
                        }
                        report.Append(i).Append(':').Append(bones[i] == null ? "(null)" : bones[i].name);
                    }
                    report.Append('\n');
                    shown++;
                }
            }
        }

        // --- Q5: how big is a turn of poses, really? ---

        /// <remarks>
        /// The design doc estimates ~44 KB per unit per turn and ~1.5 MB for a
        /// 30-unit combat from an assumed joint count. This measures it. The
        /// per-joint figure is position + rotation as floats, which is what a
        /// wire encoding would carry; the answer decides whether M8 chunks
        /// along unit boundaries or needs something cleverer.
        /// </remarks>
        private static void ProbeTracks(StringBuilder report)
        {
            var units = CombatReplayHelper.units;
            if (units == null || units.Count == 0)
            {
                report.Append("  no recorded tracks — run this after an execution\n");
                return;
            }

            const int bytesPerJoint = 28;
            long total = 0;

            foreach (var pair in units)
            {
                var track = pair.Value;
                if (track == null)
                {
                    continue;
                }

                var poses = track.keyframesPoses?.Count ?? 0;
                var joints = 0;
                if (poses > 0 && track.keyframesPoses![0].joints != null)
                {
                    joints = track.keyframesPoses[0].joints.Length;
                }

                // Every pose is re-checked rather than trusting the first: an
                // array that changes length mid-turn is exactly the condition
                // that makes ApplyTimeToUnit's unguarded loop throw.
                var ragged = false;
                if (track.keyframesPoses != null)
                {
                    foreach (var pose in track.keyframesPoses)
                    {
                        var length = pose.joints?.Length ?? -1;
                        if (length != joints)
                        {
                            ragged = true;
                            break;
                        }
                    }
                }

                var bytes = (long)poses * joints * bytesPerJoint;
                total += bytes;

                report.Append("  id=").Append(pair.Key)
                    .Append(" poses=").Append(poses)
                    .Append(" joints=").Append(joints)
                    .Append(ragged ? " RAGGED" : string.Empty)
                    .Append(" transforms=").Append(track.keyframesTransform?.Count ?? 0)
                    .Append(" states=").Append(track.keyframesStates?.Count ?? 0)
                    .Append(" destructions=").Append(track.keyframesDestructions?.Count ?? 0)
                    .Append(" lightsW=").Append(track.keyframesLightsWeapons?.Count ?? 0)
                    .Append(" melee=").Append(track.entitiesMelee?.Count ?? 0)
                    .Append(" advParticles=").Append(track.advParticleSystems?.Count ?? 0)
                    .Append(" | poseBytes=").Append(bytes)
                    .Append('\n');
            }

            report.Append("  TOTAL pose bytes across ").Append(units.Count)
                .Append(" tracks = ").Append(total)
                .Append(" (").Append((total / 1024f).ToString("0.#")).Append(" KB)\n");
        }

        private static string F(float f) => f.ToString("0.####");

        internal static void RegisterConsoleCommands()
        {
            var method = typeof(ReplayProbeGlue).GetMethod(
                nameof(ReplayProbe), BindingFlags.Static | BindingFlags.Public, null, new Type[0], null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.replay-probe"));
        }
    }
}
