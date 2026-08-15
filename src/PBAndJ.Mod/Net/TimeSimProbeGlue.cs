using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// THROWAWAY. Whether a client can render a replayed pooled effect at all,
    /// and whether the frozen <c>_TimeSimulation</c> shader global spoils it.
    /// </summary>
    /// <remarks>
    /// M14's revision 4 names this as the one open question that could still
    /// reshape the design, and it is the kind that cannot be settled by reading:
    /// the shaders are compiled assets, not decompiled C#, so whether any of
    /// them samples <c>_TimeSimulation</c> is unknowable from source.
    /// <para>
    /// Three writers set it and a client reaches none of them:
    /// <c>ActionRecordingSystem.cs:42</c> is gated on <c>combat.Simulating</c>,
    /// <c>CombatReplayHelper.cs:970</c> is inside <c>ApplyTime</c>, and
    /// <c>ShaderHelper.cs:73</c> is editor-only. So it holds whatever it last
    /// held — on a client, whatever the save's load left behind.
    /// </para>
    /// <para>
    /// The spike deliberately uses <see cref="DataContainerAssetPool.GetInstanceStandalone"/>
    /// rather than the pool ring, because revision 4 recommends exactly that and
    /// it has never been run. It therefore prices the instantiate churn at the
    /// same time as answering the shader question.
    /// </para>
    /// <para>
    /// And it drives <c>AssetLinker.SampleForReplay</c> per frame rather than
    /// letting the effect play. That is what vanilla replay does
    /// (<c>AssetLinker.cs:576</c> → <c>ParticleSystem.Simulate</c>) and it is why
    /// a client's <c>Time.timeScale</c> of zero does not freeze a replayed
    /// effect. If this spike animates, that whole class of worry is closed.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class TimeSimProbeGlue
    {
        private static readonly int ShaderIdTimeSimulation = Shader.PropertyToID("_TimeSimulation");

        private static ReplayEntityAssetStandalone? track;
        private static string? spikedKey;
        private static float elapsed;
        private static float duration;
        private static float loopLength;
        private static bool mirrorTimeSim;
        private static int frames;
        private static float restoreTimeSim;

        /// <summary>Reads the global and the two clocks that ought to drive it.</summary>
        public static string TimeSimProbe()
        {
            var combat = Contexts.sharedInstance.combat;
            var sb = new StringBuilder();
            sb.Append("[pb-and-j] timesim-probe | _TimeSimulation=")
                .Append(Num(Shader.GetGlobalFloat(ShaderIdTimeSimulation)))
                .Append(" | simTime=")
                .Append(Num(combat.hasSimulationTime ? combat.simulationTime.f : -1f))
                .Append(" simulating=").Append(combat.Simulating)
                .Append(" | Time.time=").Append(Num(Time.time))
                .Append(" timeScale=").Append(Num(Time.timeScale))
                .Append(" | state=").Append(IDUtility.IsGameState("combat") ? "combat" : "other")
                .Append(" | spike=").Append(track == null ? "idle" : spikedKey);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <summary>
        /// Spawns one pooled effect as a standalone instance and samples it
        /// forward for a few seconds, exactly as replay would.
        /// </summary>
        /// <remarks>
        /// Run it twice on the same machine — <c>mirror = 0</c> then
        /// <c>mirror = 1</c> — and compare by eye. Identical means no shader in
        /// that effect samples <c>_TimeSimulation</c> and M14 needs no mirror;
        /// different means the mirror is load-bearing and belongs in
        /// <c>KeyframePlayer</c> beside the cursor.
        /// </remarks>
        public static string FxSpike(string assetKey, float seconds, int mirror, float scale)
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }
            if (track != null)
            {
                return "[pb-and-j] a spike is already running — wait for it to finish";
            }

            var entry = DataMultiLinker<DataContainerAssetPool>.GetEntry(assetKey, false);
            if (entry == null)
            {
                return $"[pb-and-j] no asset pool '{assetKey}'";
            }

            // In front of whatever the player is looking at, because the answer
            // is visual and an effect behind the camera answers nothing.
            var camera = Camera.main;
            if (camera == null)
            {
                return "[pb-and-j] no main camera — cannot place the spike where it can be seen";
            }
            var at = camera.transform.position + (camera.transform.forward * 9f);

            // Standalone, so nothing here can touch instanceCountUsed or steal a
            // ring instance from the live game. That is the whole point of the
            // route being tested.
            var linker = entry.GetInstanceStandalone();
            if (linker == null)
            {
                return $"[pb-and-j] pool '{assetKey}' would not instantiate (no prefab?)";
            }

            // ⚠️ Both of these were missing from the first version of this probe
            // and each alone made it invisible. GetInstanceStandalone does NOT
            // SetActive (GetInstance does, DataContainerAssetPool.cs:192), and
            // SampleForReplay returns immediately unless linkedToReplay is set,
            // which only SetupForReplay does — reached here through the track's
            // AssignAsset. A driver without a track cannot sample at all.
            linker.SetActive(true);

            var size = scale > 0f ? scale : 1f;
            track = new ReplayEntityAssetStandalone
            {
                assetKey = assetKey,
                // Computed locally, never sent: string.GetHashCode is not stable
                // across processes and the steal comparison comes to depend on it.
                assetKeyHash = assetKey.GetHashCode(),
                timeStart = 0f,
                timeEnd = seconds > 0f ? seconds : 3f,
                position = at,
                rotation = Quaternion.identity,
                // The other invisibility trap: AssignAsset writes localScale from
                // THIS field, so a default-constructed track scales the effect to
                // zero and renders nothing at all.
                scale = Vector3.one * size,
                velocityAndDecay = Vector4.zero,
                parentPresent = false,
            };
            track.AssignAsset(linker);

            spikedKey = assetKey;
            duration = seconds > 0f ? seconds : 3f;
            // Replay the effect's own lifetime on a loop so it fires repeatedly
            // instead of once — one burst inside a fifteen-second window is easy
            // to miss, and "I saw nothing" then cannot be told from "it broke".
            loopLength = entry.lifetimeUsed && entry.lifetime > 0f ? entry.lifetime : 2f;
            elapsed = 0f;
            frames = 0;
            mirrorTimeSim = mirror != 0;
            restoreTimeSim = Shader.GetGlobalFloat(ShaderIdTimeSimulation);

            var line = $"[pb-and-j] fx-spike '{assetKey}' for {Num(duration)}s | scale {Num(size)} "
                + $"| looping every {Num(loopLength)}s | at {Num(at.x)},{Num(at.y)},{Num(at.z)} "
                + $"| camera '{camera.name}' | mirror={mirrorTimeSim} "
                + $"| _TimeSimulation now {Num(restoreTimeSim)}";
            Debug.Log(line);
            return line;
        }

        /// <summary>Per-frame, from the same postfix <c>KeyframePlayer</c> uses.</summary>
        internal static void Advance(float deltaSeconds)
        {
            if (track == null)
            {
                return;
            }

            elapsed += deltaSeconds;
            frames++;

            if (mirrorTimeSim)
            {
                // What M14 would do: keep the shader global moving with the
                // playback cursor, since nothing else on a client ever will.
                Shader.SetGlobalFloat(ShaderIdTimeSimulation, elapsed);
            }

            if (elapsed >= duration)
            {
                var asset = track.GetAsset();
                Debug.Log($"[pb-and-j] fx-spike '{spikedKey}' done after {frames} frames, "
                    + $"{Num(elapsed)}s — destroying");
                track.UnlinkAsset(true);
                if (asset != null)
                {
                    // Object.Destroy directly rather than ReturnInstance: the pool
                    // logs a warning per standalone return and then destroys it
                    // anyway (DataContainerAssetPool.cs:203-208).
                    Object.Destroy(asset.gameObject);
                }
                track = null;
                spikedKey = null;
                if (mirrorTimeSim)
                {
                    // The mirror leaves a residue otherwise — measured: the global
                    // sat at the playback cursor's last value after the spike
                    // ended. M14 must make the same restore a decision, not an
                    // accident.
                    Shader.SetGlobalFloat(ShaderIdTimeSimulation, restoreTimeSim);
                }
                return;
            }

            // Through the game's own track, which is the whole shape M14 would
            // use: ApplyTime subtracts timeStart and calls SampleForReplay.
            // Looped, so the effect fires again and again rather than once.
            track.ApplyTime(elapsed % loopLength);
        }

        private static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static void RegisterConsoleCommands()
        {
            var probe = typeof(TimeSimProbeGlue).GetMethod(
                nameof(TimeSimProbe), BindingFlags.Static | BindingFlags.Public,
                null, new System.Type[0], null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(probe, "pbj.timesim-probe"));

            var spike = typeof(TimeSimProbeGlue).GetMethod(
                nameof(FxSpike), BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string), typeof(float), typeof(int), typeof(float) }, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(spike, "pbj.fx-spike"));
        }
    }
}
