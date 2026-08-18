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
            // one. See the negative test in Program.cs's --selftest-guard mode,
            // which is proven to fail before it is trusted.
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

            Console.WriteLine($"[selftest] ALL PASS ({scenarios.Length} scenarios)");
            return 0;
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
