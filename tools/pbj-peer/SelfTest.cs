using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Threading;
using PBAndJ.Core.Net;
using PBAndJ.Net;

namespace PBAndJ.Peer
{
    /// <summary>
    /// Runs a host and a client in one process over real loopback sockets and
    /// walks the whole turn cycle: connect, handshake, order, ready, commit,
    /// complete.
    /// </summary>
    /// <remarks>
    /// Not part of the 100% gate — it is an integration smoke test, not a unit
    /// test, and it involves real sockets and real timing. It gates
    /// <c>make deploy</c> instead, so a broken protocol can never reach the game.
    /// <para>
    /// Its value is that it exercises the same PbjRuntime, HostSession and
    /// ClientSession the game does, over the same transports, with no game
    /// installed.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static partial class SelfTest
    {
        private const int TimeoutSeconds = 10;

        internal static int Run()
        {
            // 🔴 WHICH BUILD WAS THIS? The verdict line below used to be
            // `ALL PASS (N scenarios)` and nothing else, which made a green run
            // at protocol v10 CHARACTER-IDENTICAL to a green run at v9 -- and
            // one was duly credited with answering a question it never asked.
            // A stale build, or a run out of the wrong directory, printed the
            // same confident PASS. The run now states the build it was, and it
            // HALTS rather than guess if it cannot tell.
            if (!TryResolveBuild(out var protocolVersion, out var modVersion, out var whyNot))
            {
                Console.WriteLine($"[selftest] FATAL: this run cannot say which build it is -- {whyNot}");
                return 1;
            }

            var scenarios = new (string Name, Func<int> Body)[]
            {
                ("turn cycle", RunTurnCycle),
                ("send order", RunSendOrder),
                ("backpressure", RunBackpressure),
                ("reconnect", RunReconnect),
                ("keyframe stream", RunKeyframeStream),
                ("pose fallbacks", RunPoseFallbacks),
                ("asset fallbacks", RunAssetFallbacks),
                ("remote guards", RunRemoteGuards),
                ("scenario transfer", RunScenarioTransfer),
                ("lobby barrier", RunLobbyBarrier),
                ("combat entry", RunCombatEntry),
            };

            // 🔴 THE REGISTRATION CHECK, and it is not decoration.
            //
            // The array above is a hand-maintained list, which is a claim about
            // the world that nothing checks and which reads as data. Nothing
            // downstream counts what ran: the runner iterates whatever is here
            // and prints ALL PASS. Drop one line -- exactly what a split of this
            // 3000-line file would do -- and the suite passes with ten
            // scenarios, silently, reporting success for work it never did.
            // That is worse than a stale constant, because there is not even a
            // number to go stale.
            //
            // DERIVED, never a hardcoded count. A `private static int RunX()` in
            // this class IS a scenario, so the set is read back off the type and
            // compared with what was registered. A constant would drift on every
            // legitimate addition and would answer only half the question; this
            // catches BOTH directions -- a scenario registered but not written,
            // and a scenario written but never registered, which is the silent
            // one. Both directions were made to fail by hand before this guard
            // was trusted (8a928b7). 🔴 The comment that stood here cited "the
            // negative test in Program.cs's --selftest-guard mode": THERE IS NO
            // SUCH MODE and there never was -- 8a928b7 touched this file only,
            // and `grep -rn selftest-guard` matched nothing but the citation
            // itself. Corrected 2026-08-22 (item B10).
            //
            // Length > 3 excludes Run itself: BindingFlags.NonPublic matches
            // `internal` as well as `private`, so the entry point matched its own
            // filter and the guard reported the runner as an unregistered
            // scenario -- on every run, including a healthy one. The negative
            // test found that before the guard was trusted, which is the entire
            // argument for writing the negative test first.
            var declared = typeof(SelfTest)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Run", StringComparison.Ordinal)
                            && m.Name.Length > 3
                            && m.ReturnType == typeof(int)
                            && m.GetParameters().Length == 0)
                .Select(m => m.Name)
                .ToList();
            var registered = scenarios.Select(s => s.Body.Method.Name).ToList();
            var missing = declared.Except(registered).OrderBy(n => n).ToList();
            var phantom = registered.Except(declared).OrderBy(n => n).ToList();
            if (missing.Count > 0 || phantom.Count > 0)
            {
                Console.WriteLine("[selftest] FATAL: the scenario table does not match this class.");
                foreach (var n in missing)
                {
                    Console.WriteLine($"[selftest]   written but NEVER REGISTERED: {n}");
                }
                foreach (var n in phantom)
                {
                    Console.WriteLine($"[selftest]   registered but not declared here: {n}");
                }
                Console.WriteLine("[selftest]   A split of this file drops registration lines silently.");
                return 1;
            }

            foreach (var scenario in scenarios)
            {
                Console.WriteLine($"[selftest] --- {scenario.Name} ---");
                var code = scenario.Body();
                if (code != 0)
                {
                    Console.WriteLine($"[selftest] FAILED in '{scenario.Name}'");
                    return code;
                }
            }

            Console.WriteLine(
                $"[selftest] ALL PASS ({scenarios.Length} scenarios, "
                + $"protocol v{protocolVersion}, mod {modVersion})");
            return 0;
        }

        /// <summary>
        /// Reads the protocol identity out of the PBAndJ.Core this process
        /// actually loaded, so the verdict line can say which build it was.
        /// </summary>
        /// <remarks>
        /// 🔴 NEVER a literal here, and not quite a plain reference either.
        /// <c>PbjProtocol.Version</c> and <c>PbjProtocol.ModVersion</c> are
        /// <c>const</c>, so writing them in this file bakes a copy into
        /// pbj-peer.dll at compile time: that reports the build of the HARNESS,
        /// not of the Core the scenarios exercise. Those two differ in exactly
        /// the stale-build case this whole line exists to expose. So the numbers
        /// printed come off the loaded assembly's own metadata, and the
        /// compile-time literals are kept only as the thing to disagree with.
        /// <para>
        /// It HALTS rather than printing "unknown", and it halts at the CAUSE:
        /// a peer running against a Core it was not built against has already
        /// invalidated every scenario below, and a run that prints a version it
        /// cannot stand behind would reproduce the defect being fixed one level
        /// down. Detecting is not halting.
        /// </para>
        /// </remarks>
        private static bool TryResolveBuild(out int protocolVersion, out string modVersion, out string whyNot)
        {
            protocolVersion = 0;
            modVersion = string.Empty;

            var core = typeof(PbjProtocol).Assembly;
            var versionField = typeof(PbjProtocol).GetField(
                nameof(PbjProtocol.Version), BindingFlags.Public | BindingFlags.Static);
            var modField = typeof(PbjProtocol).GetField(
                nameof(PbjProtocol.ModVersion), BindingFlags.Public | BindingFlags.Static);
            if (versionField == null || modField == null)
            {
                whyNot = $"PbjProtocol in {core.Location} has no Version/ModVersion constant to read";
                return false;
            }

            if (versionField.GetRawConstantValue() is not int loadedVersion
                || modField.GetRawConstantValue() is not string loadedMod)
            {
                whyNot = $"PbjProtocol.Version/ModVersion in {core.Location} are not the constants this expects";
                return false;
            }

            // The compile-time literals, inlined into this assembly. Equal to the
            // loaded ones in every healthy run; unequal exactly when the harness
            // and the Core under test are different builds.
            if (loadedVersion != PbjProtocol.Version
                || !string.Equals(loadedMod, PbjProtocol.ModVersion, StringComparison.Ordinal))
            {
                whyNot = $"stale build -- this peer was compiled against protocol v{PbjProtocol.Version} "
                    + $"mod {PbjProtocol.ModVersion}, but the Core it loaded ({core.Location}) "
                    + $"is protocol v{loadedVersion} mod {loadedMod}";
                return false;
            }

            protocolVersion = loadedVersion;
            modVersion = loadedMod;
            whyNot = string.Empty;
            return true;
        }


        /// <summary>
        /// Echoes and remembers. Remembering is what lets the self-test assert on
        /// messages the session reports but does not expose as state.
        /// </summary>
        private sealed class PrefixedLog : IPbjLog
        {
            private readonly string who;
            private readonly List<string> lines = new List<string>();

            public PrefixedLog(string who) => this.who = who;

            public void Log(string line)
            {
                lines.Add(line);
                Console.WriteLine($"  [{who}] {line}");
            }

            public bool Saw(string fragment) => lines.Exists(l => l.Contains(fragment));
        }
    }
}
