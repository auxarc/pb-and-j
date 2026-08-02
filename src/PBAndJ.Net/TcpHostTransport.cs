using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PBAndJ.Core.Net;

namespace PBAndJ.Net
{
    /// <summary>
    /// Host-side TCP transport: one accept thread, and one receive plus one send
    /// thread per connection.
    /// </summary>
    /// <remarks>
    /// Excluded from the coverage gate by design — this is the socket-and-thread
    /// glue the humble-object split puts outside it. Every protocol decision
    /// happens in PBAndJ.Core, on the main thread; the threads in here only ever
    /// touch a Socket, a byte[] and the mailbox.
    /// <para>
    /// Sends are queued to a per-peer <see cref="PeerWriter"/> rather than
    /// written inline, so an unresponsive peer can never stall the pump. See that
    /// class for the backpressure policy and why closes go through the same
    /// queue.
    /// </para>
    /// <para>
    /// Blocking reads and writes only: no async, no SocketAsyncEventArgs, no
    /// Select. Under Wine the least exotic path is the best-tested one, which
    /// M4 Step 0a confirmed works.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public sealed class TcpHostTransport : IPbjTransport
    {
        private const int ReceiveBufferSize = 8192;

        private readonly IPbjInbox inbox;
        private readonly TcpListener listener;
        private readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
        private readonly ConcurrentDictionary<int, PeerWriter> writers = new ConcurrentDictionary<int, PeerWriter>();

        private int nextPeerId;
        private volatile bool running;

        public TcpHostTransport(IPbjInbox inbox, IPAddress bindAddress, int port)
        {
            this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
            listener = new TcpListener(bindAddress ?? throw new ArgumentNullException(nameof(bindAddress)), port);
        }

        /// <summary>The port actually bound — meaningful after <see cref="Start"/>.</summary>
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Start()
        {
            listener.Start();
            running = true;
            StartThread("pbj-accept", AcceptLoop);
        }

        /// <summary>
        /// Queues one frame for a peer. Returns immediately — the per-peer
        /// writer thread does the blocking write.
        /// </summary>
        public void Send(int peerId, byte[] frame)
        {
            if (!writers.TryGetValue(peerId, out var writer))
            {
                inbox.Post(new TransportLogEvent(NetLog.SendAfterStop(peerId)));
                return;
            }
            writer.Enqueue(frame);
        }

        /// <summary>
        /// Closes a peer's connection <em>behind</em> anything already queued for
        /// it, so a Reject still reaches the peer it explains.
        /// </summary>
        public void Disconnect(int peerId, string? reason)
        {
            if (writers.TryGetValue(peerId, out var writer))
            {
                writer.EnqueueClose(reason ?? "disconnected");
                return;
            }
            // No writer: nothing was ever queued, so close directly.
            if (clients.TryRemove(peerId, out var client))
            {
                Close(client);
            }
        }

        public void Stop()
        {
            running = false;
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // already down
            }
            foreach (var peerId in writers.Keys)
            {
                if (writers.TryGetValue(peerId, out var writer))
                {
                    writer.Stop();
                }
            }
            foreach (var peerId in clients.Keys)
            {
                if (clients.TryRemove(peerId, out var client))
                {
                    Close(client);
                }
            }
        }

        /// <summary>Called by a writer thread once it has finished with a peer.</summary>
        private void OnWriterClosed(int peerId, string reason)
        {
            writers.TryRemove(peerId, out _);
            if (clients.TryRemove(peerId, out var client))
            {
                Close(client);
            }
        }

        private void AcceptLoop()
        {
            try
            {
                while (running)
                {
                    var client = listener.AcceptTcpClient();
                    var peerId = Interlocked.Increment(ref nextPeerId);
                    client.NoDelay = true;
                    client.SendTimeout = 1000;
                    clients[peerId] = client;

                    var writer = new PeerWriter(peerId, client, inbox, OnWriterClosed);
                    writers[peerId] = writer;
                    writer.Start();

                    var remote = client.Client.RemoteEndPoint?.ToString();
                    inbox.Post(new PeerConnectedEvent(peerId, remote));
                    StartThread("pbj-recv-" + peerId, () => ReceiveLoop(peerId, client));
                }
            }
            catch (Exception e)
            {
                if (running)
                {
                    inbox.Post(new TransportFailedEvent(e.GetType().Name + ": " + e.Message));
                }
            }
        }

        private void ReceiveLoop(int peerId, TcpClient client)
        {
            var buffer = new byte[ReceiveBufferSize];
            string reason = "closed";
            try
            {
                var stream = client.GetStream();
                while (running)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    // Copy before posting: the buffer is reused next iteration.
                    var copy = new byte[read];
                    Buffer.BlockCopy(buffer, 0, copy, 0, read);
                    inbox.Post(new PeerBytesEvent(peerId, copy));
                }
            }
            catch (Exception e)
            {
                reason = e.GetType().Name;
            }
            finally
            {
                // Release the writer too, or its thread waits forever on a queue
                // nobody will ever feed again.
                if (writers.TryGetValue(peerId, out var writer))
                {
                    writer.EnqueueClose(reason);
                }
                clients.TryRemove(peerId, out _);
                Close(client);
                inbox.Post(new PeerDisconnectedEvent(peerId, reason));
            }
        }

        private static void Close(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // teardown is best-effort
            }
        }

        private static void StartThread(string name, ThreadStart body)
        {
            // Background so a wedged socket can never block process exit.
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }
    }
}
