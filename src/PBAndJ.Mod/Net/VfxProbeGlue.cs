using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M14's measurement bench: what a turn's effects weigh, what is alive right
    /// now, whether two installs agree on their pool table, and what the
    /// <c>_TimeSimulation</c> shader global holds.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only <see cref="VfxProbe"/> is throwaway</b> — it answered the
    /// wire-volume question before M14's wire existed and its remarks below are
    /// kept for that record. The rest earned their place by answering questions
    /// eyes could not: <see cref="FxInstances"/> is the leak detector,
    /// <see cref="FxPools"/> is the cross-install comparison, and
    /// <see cref="FxMirror"/> with <see cref="FxTimeSim"/> are the two arms of
    /// the beam A/B.
    /// <para>
    /// §8 of <c>docs/notes/replay-handoff-recon.md</c> says the shapes are
    /// almost all plain data and names the live references that are not — and
    /// then says, in as many words, that **volume has never been measured**.
    /// Designing a wire around an unmeasured volume is how M6 nearly shipped a
    /// frame the receiver would have rejected as malformed, so measure first.
    /// </para>
    /// <para>
    /// Run it on the <b>host</b>, immediately after executing a turn. A client
    /// will report zeroes for everything: every recording path is gated on
    /// <c>recordingAllowed</c>, which is only ever true during the host's own
    /// simulation. That zero is worth seeing once rather than assuming.
    /// </para>
    /// <para>
    /// The byte figures are estimates from field counts, not from an encoder.
    /// They are for deciding whether this milestone needs chunking at all, which
    /// is a question about order of magnitude rather than exact size.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class VfxProbeGlue
    {
        // A transform key is time + position + rotation: 8 floats on our wire
        // today (TransformKey is 32 bytes). Trail points are far heavier — 3
        // vectors, a tangent, a normal, a colour and three scalars.
        private const int TransformKeyBytes = 32;
        private const int TrailPointBytes = 76;
        private const int BeamKeyBytes = 44;
        private const int StandaloneBytes = 80;

        private static readonly int ShaderIdTimeSimulation =
            Shader.PropertyToID("_TimeSimulation");

        /// <summary>
        /// How many pooled instances are alive right now. M14's leak detector.
        /// </summary>
        /// <remarks>
        /// Built because <b>eyes cannot answer this one</b>, and finding that out
        /// is what it is for. A leaked replay instance sits at exactly the
        /// position the live effect that preceded it occupies, so a leak is
        /// invisible by construction: replay a turn twice and the second set
        /// hides precisely under the first. Observed on a real game, 2026-08-15
        /// — the user could see bullets suspended in the air and could not tell
        /// whose they were.
        /// <para>
        /// <c>standaloneLive</c> is the number that matters. Our replay checks
        /// out through <c>GetInstanceStandalone</c>, so every instance we hold
        /// is in some pool's <c>instancesStandalone</c>. It is not <i>only</i>
        /// ours — <c>UnitVFXManagerBase:272</c> and <c>ScenarioUtility:3821</c>
        /// use that route too — so read it as a <b>delta across one replay</b>
        /// rather than as an absolute. Zero delta is the sweep working; a delta
        /// near the turn's effect count is the sweep not running at all.
        /// </para>
        /// <para>
        /// <c>listed</c> counts the same lists including destroyed entries.
        /// Destroying a standalone instance leaves its slot behind — Unity's
        /// fake-null makes it compare equal to null and the pool's own
        /// teardown sweep skips it — so <c>listed</c> climbing while
        /// <c>standaloneLive</c> holds steady is exactly what correct behaviour
        /// looks like, and is worth being able to see rather than fear.
        /// </para>
        /// </remarks>
        public static string FxInstances()
        {
            var pools = DataMultiLinker<DataContainerAssetPool>.data;
            if (pools == null)
            {
                return "[pb-and-j] fx-instances | no asset pools are loaded";
            }

            var live = 0;
            var listed = 0;
            var poolsWithLive = 0;
            var pooledUsed = 0;

            foreach (var pair in pools)
            {
                var pool = pair.Value;
                if (pool == null)
                {
                    continue;
                }
                pooledUsed += pool.instanceCountUsed;

                var instances = pool.instancesStandalone;
                if (instances == null)
                {
                    continue;
                }

                var here = 0;
                for (var i = 0; i < instances.Count; i++)
                {
                    // Unity's overloaded == , deliberately: a destroyed object is
                    // not a null reference and only this comparison knows it.
                    if (instances[i] != null)
                    {
                        here++;
                    }
                }

                listed += instances.Count;
                live += here;
                if (here > 0)
                {
                    poolsWithLive++;
                }
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] fx-instances | standaloneLive={0} across {1} pool(s) | listed={2} | pooledUsed={3}",
                live, poolsWithLive, listed, pooledUsed);
        }

        /// <summary>
        /// A comparable fingerprint of this install's asset pool table.
        /// </summary>
        /// <remarks>
        /// M14 measurement 3. "Asset keys resolve on the client" rests entirely
        /// on the handshake refusing a mismatched game build and mod version, and
        /// DLC or workshop content can diverge at identical versions — so the
        /// claim has a hole that only a comparison between two machines closes.
        /// Run it on both and compare one line.
        /// <para>
        /// ⚠️ <b><c>prefabNull</c> is the field that stops a match being read as
        /// more than it is.</b> <c>DataContainerAssetPool.OnAfterDeserialization</c>
        /// (<c>:60-66</c>) keeps its entry when <c>Resources.Load</c> fails — it
        /// logs a warning and moves on with a null prefab — so two machines can
        /// agree on every key while one of them cannot instantiate a pool at all
        /// (<c>GetInstanceStandalone</c> bails at <c>:160-162</c>). A digest match
        /// alone would be confidently wrong in exactly the case this exists to
        /// catch.
        /// </para>
        /// <para>
        /// The sorted key list is written out beside the digest, because a
        /// mismatch has to be actionable: sixteen hex characters say the installs
        /// differ and nothing about how. Written to the settings folder rather
        /// than the mod folder, which <c>make deploy</c> deletes.
        /// </para>
        /// </remarks>
        public static string FxPools()
        {
            var pools = DataMultiLinker<DataContainerAssetPool>.data;
            if (pools == null)
            {
                return "[pb-and-j] fx-pools | no asset pools are loaded";
            }

            var keys = new List<string>(pools.Count);
            var prefabNull = 0;
            foreach (var pair in pools)
            {
                keys.Add(pair.Key);
                // Unity's overloaded ==, as everywhere else here: a prefab whose
                // load failed is a fake-null, not a null reference.
                if (pair.Value == null || pair.Value.prefab == null)
                {
                    prefabNull++;
                }
            }

            var fingerprint = AssetPoolDigest.Compute(keys);
            var written = WriteKeyList(keys);

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] fx-pools | count={0} | digest={1} | prefabNull={2} | keys written to {3}",
                fingerprint.Count, fingerprint.Digest, prefabNull, written ?? "(nowhere)");
            Debug.Log(line);
            return line;
        }

        private static string? WriteKeyList(List<string> keys)
        {
            try
            {
                var folder = DataPathHelper.GetSettingsFolder();
                if (string.IsNullOrEmpty(folder))
                {
                    return null;
                }

                // Sorted here as well as inside the digest, so the file and the
                // number are answers to the same question and a diff of two files
                // reads as a diff of two key sets.
                var sorted = new List<string>(keys);
                sorted.Sort(StringComparer.Ordinal);

                var path = Path.Combine(folder, "pb-and-j.asset-pools.txt");
                File.WriteAllLines(path, sorted.ToArray());
                return path;
            }
            catch (Exception e)
            {
                // The digest is the measurement; the file is the follow-up. A
                // read-only or missing settings folder must not cost the reading.
                Debug.LogWarning(
                    "[pb-and-j] could not write the pool key list: "
                        + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Reads <c>_TimeSimulation</c>, the client's real precondition.</summary>
        /// <remarks>
        /// M14 measurement 2. This number is what the whole A/B is about: a host
        /// that has just executed leaves the global at its own end-of-turn
        /// simulation time, which is very nearly the right answer, while a client
        /// reaches none of the writers and holds whatever was last left there —
        /// measured once as a stale <b>overworld</b> value of 49.12.
        /// <para>
        /// Read it on the client BEFORE replaying. A baseline that turns out to
        /// sit within a second of the window's end means the run is not measuring
        /// the stale case at all.
        /// </para>
        /// </remarks>
        public static string FxTimeSim()
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] fx-tsim | _TimeSimulation={0} | mirror={1} | Time.timeScale={2}",
                Num(Shader.GetGlobalFloat(ShaderIdTimeSimulation)),
                KeyframePlayer.MirrorTimeSimulation ? "on" : "off",
                Num(Time.timeScale));
            Debug.Log(line);
            return line;
        }

        /// <summary>Forces <c>_TimeSimulation</c>, to stage a precondition.</summary>
        /// <remarks>
        /// For emulating a client's stale baseline on a host, where the A/B is
        /// otherwise run at its point of minimum sensitivity — the host's own
        /// baseline is already nearly correct, so both arms look alike whatever
        /// the shaders do. Not a fix for that; the two-instance run is. This only
        /// makes a host-side rehearsal mean something.
        /// <para>
        /// ⚠️ Whatever is set here survives until something writes the global
        /// again. Playback's own restore hands back what it found, so setting
        /// this mid-window is undone at the end of it.
        /// </para>
        /// </remarks>
        public static string FxTimeSimSet(float value)
        {
            Shader.SetGlobalFloat(ShaderIdTimeSimulation, value);
            var line = "[pb-and-j] fx-tsim-set | _TimeSimulation now " + Num(value);
            Debug.Log(line);
            return line;
        }

        /// <summary>Turns the playback <c>_TimeSimulation</c> mirror on or off.</summary>
        /// <remarks>
        /// Off is the shipped behaviour and the default. Echoes the resulting
        /// state rather than staying silent — a toggle that reports nothing is
        /// the silent-success bug the connect screen already paid for once, and
        /// here it would be worse: the two arms of the A/B are told apart only by
        /// this flag, so a run under a misremembered setting produces a confident
        /// answer to the wrong question.
        /// </remarks>
        public static string FxMirror(int on)
        {
            KeyframePlayer.MirrorTimeSimulation = on != 0;
            var line = "[pb-and-j] fx-mirror | _TimeSimulation mirror is now "
                + (KeyframePlayer.MirrorTimeSimulation ? "ON" : "OFF")
                + " | replay a turn to compare";
            Debug.Log(line);
            return line;
        }

        /// <summary>Freezes replay at a given time, or releases it.</summary>
        /// <remarks>
        /// Set it <b>before</b> <c>pbj.replay-last</c>, not during — a command
        /// racing a five-second window would decide for itself whether the hold
        /// took. A negative value releases, and the window then runs to its end
        /// and sweeps normally.
        /// <para>
        /// This exists because the first attempt at measurement 2 returned
        /// "could not tell": one beam among 378 effects, compared across two
        /// five-second replays from memory. Held, the comparison becomes a still
        /// image with one variable — move <c>_TimeSimulation</c> with
        /// <c>pbj.fx-tsim-set</c> and nothing else on screen changes, so a
        /// difference can be photographed and diffed instead of judged.
        /// </para>
        /// <para>
        /// ⚠️ A held window never ends, so it never sweeps: its effect instances
        /// stay checked out until the hold is released or combat tears down.
        /// </para>
        /// </remarks>
        public static string FxHold(float seconds)
        {
            KeyframePlayer.HoldAt = seconds;
            if (seconds < 0f)
            {
                return "[pb-and-j] fx-hold | released — the next window will play to its end";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] fx-hold | armed at {0:0.00}s — replay now; playback will freeze there"
                    + " and NOT sweep until you call pbj.fx-hold -1",
                seconds);
        }

        public static string VfxProbe()
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }

            var sb = new StringBuilder();
            sb.Append("[pb-and-j] vfx-probe | turnStart=")
                .Append(Num(CombatReplayHelper.turnStartTime))
                .Append(" previewLimit=").Append(Num(CombatReplayHelper.previewTimeLimit))
                .Append(" experimental=").Append(CombatReplayHelper.experimentalMode);

            long bytes = 0;

            // --- projectiles ---
            var projectiles = CombatReplayHelper.assetsProjectiles;
            var projKeys = 0;
            var projTrails = 0;
            var projTrailPoints = 0;
            if (projectiles != null)
            {
                foreach (var pair in projectiles)
                {
                    var p = pair.Value;
                    if (p == null)
                    {
                        continue;
                    }
                    projKeys += p.keyframesTransform != null ? p.keyframesTransform.Count : 0;
                    if (p.keyframesTrail != null && p.keyframesTrail.Count > 0)
                    {
                        projTrails++;
                        projTrailPoints += p.keyframesTrail.Count;
                    }
                }
            }
            bytes += (long)projKeys * TransformKeyBytes;
            bytes += (long)projTrailPoints * TrailPointBytes;
            sb.Append(" | projectiles=").Append(projectiles == null ? 0 : projectiles.Count)
                .Append(" keys=").Append(projKeys)
                .Append(" withTrail=").Append(projTrails)
                .Append(" trailPoints=").Append(projTrailPoints);

            // --- beams ---
            var beams = CombatReplayHelper.assetsBeams;
            var beamKeys = 0;
            if (beams != null)
            {
                foreach (var pair in beams)
                {
                    var b = pair.Value;
                    beamKeys += b != null && b.keyframes != null ? b.keyframes.Count : 0;
                }
            }
            bytes += (long)beamKeys * BeamKeyBytes;
            sb.Append(" | beams=").Append(beams == null ? 0 : beams.Count)
                .Append(" keys=").Append(beamKeys);

            // --- standalone assets, and the one field that cannot travel ---
            var standalone = CombatReplayHelper.assetsStandalone;
            var parented = 0;
            if (standalone != null)
            {
                for (var i = 0; i < standalone.Count; i++)
                {
                    var s = standalone[i];
                    if (s != null && s.parentPresent)
                    {
                        parented++;
                    }
                }
            }
            bytes += (long)(standalone == null ? 0 : standalone.Count) * StandaloneBytes;
            sb.Append(" | standalone=").Append(standalone == null ? 0 : standalone.Count)
                .Append(" parented=").Append(parented);

            // --- per-unit: advanced particles and weapon lights ---
            // The two that carry live Transform / ParticleSystem references, so
            // the counts here are really "how much re-resolution work", not
            // "how many bytes".
            var units = CombatReplayHelper.units;
            var particleBlocks = 0;
            var presimulated = 0;
            var particleKeys = 0;
            var lights = 0;
            var lightsWithTransform = 0;
            if (units != null)
            {
                foreach (var pair in units)
                {
                    var track = pair.Value;
                    if (track == null)
                    {
                        continue;
                    }
                    if (track.advParticleSystems != null)
                    {
                        foreach (var block in track.advParticleSystems)
                        {
                            var b = block.Value;
                            if (b == null)
                            {
                                continue;
                            }
                            particleBlocks++;
                            if (b.presimulated)
                            {
                                presimulated++;
                            }
                            particleKeys += b.keyframesTransform != null ? b.keyframesTransform.Count : 0;
                            particleKeys += b.keyframesActivation != null ? b.keyframesActivation.Count : 0;
                        }
                    }
                    if (track.keyframesLightsWeapons != null)
                    {
                        lights += track.keyframesLightsWeapons.Count;
                        for (var i = 0; i < track.keyframesLightsWeapons.Count; i++)
                        {
                            var light = track.keyframesLightsWeapons[i];
                            if (light != null && light.firingTransform != null)
                            {
                                lightsWithTransform++;
                            }
                        }
                    }
                }
            }
            bytes += (long)particleKeys * TransformKeyBytes;
            sb.Append(" | particleBlocks=").Append(particleBlocks)
                .Append(" presimulated=").Append(presimulated)
                .Append(" particleKeys=").Append(particleKeys)
                .Append(" | weaponLights=").Append(lights)
                .Append(" withFiringTransform=").Append(lightsWithTransform);

            sb.Append(" | ESTIMATED ").Append(bytes).Append(" bytes (")
                .Append(Num(bytes / 1024f)).Append(" KB)");

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        private static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(VfxProbe), "pbj.vfx-probe");
            Add(nameof(FxInstances), "pbj.fx-instances");
            Add(nameof(FxPools), "pbj.fx-pools");
            Add(nameof(FxTimeSim), "pbj.fx-tsim");
            Add(nameof(FxTimeSimSet), "pbj.fx-tsim-set", typeof(float));
            Add(nameof(FxMirror), "pbj.fx-mirror", typeof(int));
            Add(nameof(FxHold), "pbj.fx-hold", typeof(float));
        }

        private static void Add(string methodName, string command, params Type[] signature)
        {
            var method = typeof(VfxProbeGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public,
                null, signature, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
