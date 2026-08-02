using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using PBAndJ.Core.Net;

namespace PBAndJ.Net
{
    /// <summary>
    /// One connection's outbound half: a bounded queue and the single thread
    /// that drains it into the socket.
    /// </summary>
    /// <remarks>
    /// Exists so a wedged peer cannot stall the Unity main thread. Before this,
    /// <c>Send</c> wrote synchronously from the pump with a 1s
    /// <see cref="Socket.SendTimeout"/>, and <c>BroadcastEffect</c> loops peers —
    /// so three unresponsive peers meant a three-second frame. That was already
    /// a bug at M4 message sizes; snapshots only make it easier to hit.
    /// <para>
    /// One writer per connection, never one shared writer: a shared queue would
    /// reintroduce head-of-line blocking across peers, which is the exact failure
    /// this removes. The host already runs one receive thread per connection, so
    /// this matches the shape that is already there.
    /// </para>
    /// <para>
    /// Hand-rolled rather than <c>BlockingCollection&lt;T&gt;</c>: that type
    /// bounds on an <em>item</em> count, and a Ping and an 11 KB snapshot are not
    /// comparable items, while its bounded mode blocks the producer — the very
    /// behaviour being removed here.
    /// </para>
    /// <para>
    /// Excluded from the coverage gate by design, like the transports that own
    /// it. It is exercised by the harness self-test's send-order and
    /// backpressure scenarios, over real loopback sockets.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal sealed class PeerWriter
    {
        /// <summary>Queued bytes past which the peer is dropped.</summary>
        internal const int MaxQueuedBytes = 4 * 1024 * 1024;

        /// <summary>Queued frames past which the peer is dropped.</summary>
        internal const int MaxQueuedFrames = 1024;

        /// <summary>Queued bytes past which a slow link is reported, once.</summary>
        internal const int BacklogWarnBytes = 256 * 1024;

        /// <summary>How long <see cref="Stop"/> waits for a queue to drain.</summary>
        internal const int DrainDeadlineMs = 250;

        private readonly object gate = new object();
        private readonly Queue<byte[]?> queue = new Queue<byte[]?>();
        private readonly int peerId;
        private readonly TcpClient client;
        private readonly IPbjInbox inbox;
        private readonly Action<int, string> onClosed;

        private long queuedBytes;
        private bool warned;
        private bool closing;
        private bool finished;

        internal PeerWriter(int peerId, TcpClient client, IPbjInbox inbox, Action<int, string> onClosed)
        {
            this.peerId = peerId;
            this.client = client;
            this.inbox = inbox;
            this.onClosed = onClosed;
        }

        internal void Start()
        {
            new Thread(WriteLoop) { IsBackground = true, Name = "pbj-send-" + peerId }.Start();
        }

        /// <summary>Queues one framed payload. Never blocks.</summary>
        internal void Enqueue(byte[] frame)
        {
            lock (gate)
            {
                if (closing)
                {
                    return;
                }

                // Never drop a frame to make room: the protocol is a stateful
                // stream with no resend and no sequence numbers, so a silently
                // dropped TurnCommit strands that client forever with nothing on
                // either side able to detect it. Over the bound, the peer goes.
                if (queue.Count >= MaxQueuedFrames || queuedBytes + frame.Length > MaxQueuedBytes)
                {
                    inbox.Post(new TransportLogEvent(
                        NetLog.SendQueueOverflowed(peerId, (int)queuedBytes, queue.Count)));
                    CloseLocked("send queue overflow");
                    return;
                }

                queue.Enqueue(frame);
                queuedBytes += frame.Length;

                if (!warned && queuedBytes >= BacklogWarnBytes)
                {
                    // Latched: a slow link should be visible before it is fatal,
                    // but not once per frame.
                    warned = true;
                    inbox.Post(new TransportLogEvent(
                        NetLog.SendQueueBacklog(peerId, (int)queuedBytes, queue.Count)));
                }

                Monitor.Pulse(gate);
            }
        }

        /// <summary>
        /// Queues a close behind everything already waiting.
        /// </summary>
        /// <remarks>
        /// Load-bearing. <c>HostSession.Reject</c> emits the Reject frame and
        /// then a disconnect; closing the socket out of band would turn every
        /// rejection into a silent RST, and a peer sending a bad protocol version
        /// would stop being told why. Routing the close through the same queue
        /// preserves Core's effect ordering with no change to Core.
        /// </remarks>
        internal void EnqueueClose(string reason)
        {
            lock (gate)
            {
                if (closing)
                {
                    return;
                }
                closing = true;
                queue.Enqueue(null);
                Monitor.Pulse(gate);
            }
        }

        /// <summary>
        /// Asks the writer to finish, waiting briefly for queued frames to land.
        /// </summary>
        /// <remarks>
        /// Bounded rather than indefinite: this runs from
        /// <c>Heartbeat.OnApplicationQuit</c>. The thread is a background thread
        /// anyway, so a wedged socket can never hold up process exit.
        /// </remarks>
        internal void Stop()
        {
            EnqueueClose("session closed");
            lock (gate)
            {
                var deadline = Environment.TickCount + DrainDeadlineMs;
                while (!finished)
                {
                    var remaining = deadline - Environment.TickCount;
                    if (remaining <= 0 || !Monitor.Wait(gate, remaining))
                    {
                        return;
                    }
                }
            }
        }

        private void CloseLocked(string reason)
        {
            closing = true;
            queue.Clear();
            queuedBytes = 0;
            queue.Enqueue(null);
            Monitor.Pulse(gate);
        }

        private void WriteLoop()
        {
            var reason = "closed";
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    byte[]? frame;
                    lock (gate)
                    {
                        while (queue.Count == 0)
                        {
                            Monitor.Wait(gate);
                        }
                        frame = queue.Dequeue();
                        if (frame != null)
                        {
                            queuedBytes -= frame.Length;
                            if (warned && queuedBytes < BacklogWarnBytes)
                            {
                                warned = false;
                            }
                        }
                    }

                    if (frame == null)
                    {
                        break;
                    }
                    stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception e)
            {
                reason = e.GetType().Name;
                inbox.Post(new TransportLogEvent(NetLog.SendFailed(peerId, reason)));
            }
            finally
            {
                lock (gate)
                {
                    closing = true;
                    finished = true;
                    Monitor.PulseAll(gate);
                }
                // The receive loop posts its own PeerDisconnectedEvent when the
                // socket closes, so a peer can produce two. Both sessions ignore
                // the second: HandleDisconnect early-returns for a peer that is
                // no longer registered, and the client's is terminal-state
                // guarded.
                onClosed(peerId, reason);
            }
        }
    }
}
