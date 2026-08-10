// Dev rig only. Absent from any build that does not define PBJ_DRIVE,
// which is every build that ships — see PBAndJ.Mod.csproj and
// `make check-no-drive-channel`.
#if PBJ_DRIVE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
// System.Diagnostics is deliberately NOT imported: it would make `Debug`
// ambiguous against UnityEngine.Debug, which every log line here uses.
// Stopwatch is qualified instead.
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// A loopback command channel, so a whole two-instance playtest can be
    /// driven without a human at each keyboard.
    /// </summary>
    /// <remarks>
    /// This is not test scaffolding. Co-op will eventually have the host drive a
    /// client through the mod, and the seams that permits are the same seams this
    /// uses — so anything this cannot drive is a finding about the mod's
    /// prospects, not about the rig. Everything here therefore goes through
    /// <see cref="QuantumConsoleProcessor.InvokeCommand"/> and the ordinary
    /// console surface. A private back door would prove nothing.
    /// <para>
    /// <b>Security.</b> A command socket is remote code execution by
    /// construction, and that is not hypothetical here: Quantum Console's Extras
    /// assembly already registers <c>exec</c> (compile and run arbitrary C#),
    /// file read/write and HTTP, and those pass its scan rule. So this binds
    /// loopback only and stays shut unless explicitly switched on for the
    /// process. It is never enabled by a console command and never persisted to
    /// settings — the README's promise that no socket opens without opt-in has to
    /// keep holding for the dev rig too, or it does not mean anything.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class DriveGlue
    {
        /// <summary>How long a caller may occupy the main thread with one command.</summary>
        private const int CommandTimeoutMs = 30000;

        /// <summary>Bounds the wait for a reply nobody is reading.</summary>
        private const int SendTimeoutMs = 5000;

        private const string EnvVar = "PBJ_DRIVE_PORT";
        private const string ArgPrefix = "--pbj-drive-port=";

        private static TcpListener? listener;
        private static Thread? acceptThread;
        private static volatile bool running;

        private static readonly ConcurrentQueue<Request> Pending = new ConcurrentQueue<Request>();

        private static StreamWriter? trace;
        private static readonly object TraceLock = new object();

        /// <summary>One command in flight, owned by the connection thread that made it.</summary>
        private sealed class Request
        {
            internal string Command = string.Empty;
            internal string Reply = string.Empty;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        /// <summary>
        /// Starts the channel if this process was launched with a port.
        /// </summary>
        /// <remarks>
        /// Two ways in, because only one of them has prior art. The launch script
        /// exports <c>SteamAppId</c> and the in-process Steamworks reads it, so
        /// shell-to-Wine environment propagation demonstrably works — but nothing
        /// in this mod has ever read an environment variable from Mono under
        /// Proton, so the command line is carried as a fallback and the log says
        /// which one answered.
        /// </remarks>
        internal static void Start()
        {
            if (running)
            {
                return;
            }

            var port = PortFromEnvironment(out var source);
            if (port <= 0)
            {
                return;
            }

            try
            {
                OpenTrace(port);
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                running = true;

                acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "pbj-drive",
                };
                acceptThread.Start();

                Say("drive channel listening on 127.0.0.1:" + port + " (port from " + source + ")");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] drive channel could not start on port "
                    + port + ": " + e.GetType().Name + ": " + e.Message);
                running = false;
            }
        }

        internal static void Stop()
        {
            if (!running)
            {
                return;
            }

            running = false;
            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
                // Shutting down; a listener that will not close cleanly is not
                // worth a warning in the log.
            }

            listener = null;
            lock (TraceLock)
            {
                trace?.Flush();
                trace?.Dispose();
                trace = null;
            }
        }

        /// <summary>
        /// Runs one queued command per frame, on the Unity main thread.
        /// </summary>
        /// <remarks>
        /// One per frame rather than draining the queue: a command can load a
        /// scene or write a save, and running several in a frame would stack
        /// those effects inside a single <c>Update</c>. The driver is a script
        /// waiting on replies, so it costs it nothing.
        /// </remarks>
        internal static void Tick()
        {
            if (!running || !Pending.TryDequeue(out var request))
            {
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = QuantumConsoleProcessor.InvokeCommand(request.Command);
                request.Reply = result == null ? string.Empty : result.ToString();
            }
            catch (Exception e)
            {
                // Never let a bad command escape into the postfix. NetGlue.Pump
                // turns a throw into "networking stopped" for the whole process,
                // and this runs beside it.
                request.Reply = "ERROR " + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                watch.Stop();
                Say("ran '" + request.Command + "' in " + watch.ElapsedMilliseconds
                    + "ms -> " + request.Reply);
                request.Done.Set();
            }
        }

        private static void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var client = listener?.AcceptTcpClient();
                    if (client == null)
                    {
                        return;
                    }

                    var thread = new Thread(() => Serve(client))
                    {
                        IsBackground = true,
                        Name = "pbj-drive-conn",
                    };
                    thread.Start();
                }
                catch (Exception)
                {
                    // Stop() closes the listener under us, which lands here.
                    return;
                }
            }
        }

        /// <summary>
        /// Serves one connection: read a line, hand it to the main thread, write
        /// the answer back.
        /// </summary>
        /// <remarks>
        /// The reply is written from <em>this</em> thread rather than from
        /// <see cref="Tick"/> on purpose. A driver that stops reading applies TCP
        /// backpressure to whoever is writing, and a blocked write inside the
        /// frame pump would stall rendering for the whole game.
        /// </remarks>
        private static void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.SendTimeout = SendTimeoutMs;
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var writer = new StreamWriter(stream, new UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };

                    string? line;
                    while (running && (line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        var request = new Request { Command = line };
                        Pending.Enqueue(request);

                        if (!request.Done.Wait(CommandTimeoutMs))
                        {
                            writer.WriteLine("TIMEOUT after " + CommandTimeoutMs + "ms");
                            writer.WriteLine(".");
                            continue;
                        }

                        // A result may be several lines; the lone dot ends it, so
                        // a driver knows when to stop reading without counting.
                        writer.WriteLine("OK");
                        foreach (var l in Lines(request.Reply))
                        {
                            writer.WriteLine(l);
                        }

                        writer.WriteLine(".");
                    }
                }
            }
            catch (Exception e)
            {
                Say("connection ended: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static IEnumerable<string> Lines(string text)
        {
            foreach (var l in text.Replace("\r\n", "\n").Split('\n'))
            {
                // A line that is just a dot would end the reply early.
                yield return l == "." ? ".." : l;
            }
        }

        private static int PortFromEnvironment(out string source)
        {
            source = "nowhere";

            try
            {
                var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrEmpty(fromEnv) && int.TryParse(fromEnv, out var envPort))
                {
                    source = "the environment";
                    return envPort;
                }
            }
            catch (Exception)
            {
                // Reading the environment is not guaranteed under Wine; fall
                // through to the command line rather than failing the channel.
            }

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg != null && arg.StartsWith(ArgPrefix, StringComparison.Ordinal)
                    && int.TryParse(arg.Substring(ArgPrefix.Length), out var argPort))
                {
                    source = "the command line";
                    return argPort;
                }
            }

            return 0;
        }

        /// <summary>
        /// Opens the per-instance trace, keeping one previous generation.
        /// </summary>
        /// <remarks>
        /// <c>Player.log</c> is not sufficient on its own:
        /// <c>docs/notes/harmony-patch-durability.md</c> records that engine logs
        /// buffer and drop their tails when a process dies, and that the decisive
        /// artifact in the investigation next door was a <em>rotated</em> copy
        /// that a truncate-on-launch policy had nearly destroyed. Flushed per
        /// line, one generation kept.
        /// </remarks>
        private static void OpenTrace(int port)
        {
            try
            {
                var dir = DataPathHelper.GetSettingsFolder();
                var path = Path.Combine(dir, "pb-and-j.drive." + port + ".log");
                var prev = path + ".prev";
                if (File.Exists(path))
                {
                    if (File.Exists(prev))
                    {
                        File.Delete(prev);
                    }

                    File.Move(path, prev);
                }

                trace = new StreamWriter(path, false) { AutoFlush = true };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] drive trace unavailable: "
                    + e.GetType().Name + ": " + e.Message);
                trace = null;
            }
        }

        private static void Say(string message)
        {
            Debug.Log("[pb-and-j] " + message);
            lock (TraceLock)
            {
                trace?.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + message);
            }
        }
    }
}
#endif
