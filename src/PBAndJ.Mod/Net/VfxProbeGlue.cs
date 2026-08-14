using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// THROWAWAY. How much a turn's projectiles, beams and VFX would weigh on
    /// the wire, and how much of it cannot cross a process boundary.
    /// </summary>
    /// <remarks>
    /// §8 of <c>docs/notes/replay-handoff-recon.md</c> says the shapes are
    /// almost all plain data and names the live references that are not — and
    /// then says, in as many words, that **volume has never been measured**.
    /// Designing a wire around an unmeasured volume is how M6 nearly shipped a
    /// frame the receiver would have rejected as malformed, so measure first.
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
            var method = typeof(VfxProbeGlue).GetMethod(
                nameof(VfxProbe), BindingFlags.Static | BindingFlags.Public,
                null, new System.Type[0], null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.vfx-probe"));
        }
    }
}
