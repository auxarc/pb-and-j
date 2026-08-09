using System.Collections;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// The host half of M12a's mirror: says where the base is, often enough for
    /// a passenger to watch it travel and rarely enough to be free.
    /// </summary>
    /// <remarks>
    /// Cadence is decided here rather than in Core, because it is a property of
    /// how the game moves rather than of the protocol — and the recon measured
    /// what the game does:
    /// <list type="bullet">
    ///   <item><b>On movement.</b> Sampled a few times a second and sent only
    ///   when the base has actually moved. The overworld clock is not
    ///   continuous — it advances only while the base travels or a time skip
    ///   runs — so a still base genuinely has nothing to report, and this stays
    ///   silent for most of a session.</item>
    ///   <item><b>Plus a slow heartbeat.</b> Not to track motion, which the
    ///   first rule already covers, but so that a client which missed an update
    ///   while the base was moving does not sit wrong indefinitely once
    ///   everything goes still. Without it, one dropped message during travel is
    ///   a permanently wrong map for that player — and the clock stopping is
    ///   exactly when nothing would ever correct it.</item>
    /// </list>
    /// Both rules go through the same event, so the client cannot tell them
    /// apart and does not need to.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class BaseMirrorGlue
    {
        /// <summary>
        /// How often the base is looked at. Fast enough that travel looks like
        /// travel rather than teleporting, and this only samples — it sends
        /// nothing unless the base moved.
        /// </summary>
        private const float SampleSeconds = 0.25f;

        /// <summary>
        /// How long a still base stays quiet before saying where it is anyway.
        /// </summary>
        private const float HeartbeatSeconds = 5f;

        /// <summary>
        /// How far the base must move to be worth a message. Comfortably below
        /// anything visible on the map, and above the float noise a position
        /// picks up from being written and validated every frame.
        /// </summary>
        private const float MovedEnough = 0.05f;

        private static bool running;
        private static Vector3 lastSent;
        private static bool everSent;
        private static float quietSeconds;

        internal static void SetCoroutineHost(MonoBehaviour behaviour)
        {
            if (running || behaviour == null)
            {
                return;
            }

            running = true;
            behaviour.StartCoroutine(Loop());
        }

        private static IEnumerator Loop()
        {
            while (true)
            {
                // Unscaled: the overworld clock is stopped for most of a
                // session, and a mirror that only ticked while the world was
                // moving could never send the heartbeat that exists precisely
                // for when it is not.
                yield return new WaitForSecondsRealtime(SampleSeconds);

                try
                {
                    Sample();
                }
                catch (System.Exception e)
                {
                    // A throw here would kill the coroutine and silently end the
                    // mirror for the rest of the process, which is exactly the
                    // sort of quiet failure this milestone is built to avoid.
                    Debug.LogWarning($"[pb-and-j] base mirror sample failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static void Sample()
        {
            if (PassengerGlue.Role != SessionRole.Host)
            {
                // Not hosting: forget what we sent, so hosting again later starts
                // by stating the position rather than assuming a client already
                // has it.
                everSent = false;
                quietSeconds = 0f;
                return;
            }

            var playerBase = IDUtility.playerBaseOverworld;
            if (playerBase == null || !playerBase.hasPosition)
            {
                return;
            }

            var position = playerBase.position.v;
            quietSeconds += SampleSeconds;

            var moved = !everSent || (position - lastSent).sqrMagnitude > MovedEnough * MovedEnough;
            if (!moved && quietSeconds < HeartbeatSeconds)
            {
                return;
            }

            // X and Z only. The height is the receiving machine's business —
            // see BasePositionMessage, which explains why at length.
            NetGlue.PostLocalBasePosition(position.x, position.z);
            lastSent = position;
            everSent = true;
            quietSeconds = 0f;
        }
    }
}
