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
    /// Host-side TCP transport: one accept thread, one receive thread per
    /// connection, and nothing else.
    /// </summary>
    /// <remarks>
    /// Excluded from the coverage gate by design — this is the socket-and-thread
    /// glue the humble-object split puts outside it. Every protocol decision
    /// happens in PBAndJ.Core, on the main thread; the threads in here only ever
    /// touch a Socket, a byte[] and the mailbox.
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

        public void Send(int peerId, byte[] frame)
        {
            if (!clients.TryGetValue(peerId, out var client))
            {
                return;
            }
            // Synchronous on the main thread: every M4 message is small and
            // SendTimeout bounds the worst case. An outbound queue is a hard
            // prerequisite before state snapshots go over the wire (see
            // docs/design/networking.md, known limitations).
            client.GetStream().Write(frame, 0, frame.Length);
        }

        public void Disconnect(int peerId, string? reason)
        {
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
            foreach (var peerId in clients.Keys)
            {
                Disconnect(peerId, "session closed");
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
