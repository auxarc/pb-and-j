using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using PBAndJ.Core.Net;

namespace PBAndJ.Net
{
    /// <summary>
    /// Client-side TCP transport: one connection to the host, one receive
    /// thread. The host is always addressed as
    /// <see cref="ClientSession.HostConnectionId"/>.
    /// </summary>
    /// <remarks>
    /// Excluded from the coverage gate, like its host counterpart. See
    /// <see cref="TcpHostTransport"/> for the threading rules.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public sealed class TcpClientTransport : IPbjTransport
    {
        private const int ReceiveBufferSize = 8192;

        private readonly IPbjInbox inbox;
        private TcpClient? client;
        private PeerWriter? writer;
        private volatile bool running;

        public TcpClientTransport(IPbjInbox inbox)
        {
            this.inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        }

        /// <summary>Connects, then posts PeerConnected so the session sends Hello.</summary>
        public void Connect(string host, int port)
        {
            client = new TcpClient { NoDelay = true, SendTimeout = 1000 };
            client.Connect(host, port);
            running = true;

            // Queued like the host's, so a stalled host cannot freeze the
            // client's frame loop either — which matters once a real game sits
            // on this side of the socket.
            writer = new PeerWriter(ClientSession.HostConnectionId, client, inbox, OnWriterClosed);
            writer.Start();

            inbox.Post(new PeerConnectedEvent(ClientSession.HostConnectionId,
                client.Client.RemoteEndPoint?.ToString()));

            new Thread(ReceiveLoop) { IsBackground = true, Name = "pbj-recv-host" }.Start();
        }

        public void Send(int peerId, byte[] frame)
        {
            var current = writer;
            if (current == null)
            {
                // Was a silent no-op before, which made a frame lost after Stop
                // impossible to diagnose.
                inbox.Post(new TransportLogEvent(NetLog.SendAfterStop(peerId)));
                return;
            }
            current.Enqueue(frame);
        }

        /// <summary>Closes behind anything already queued — a Bye still lands.</summary>
        public void Disconnect(int peerId, string? reason)
        {
            var current = writer;
            if (current == null)
            {
                Stop();
                return;
            }
            current.EnqueueClose(reason ?? "disconnected");
        }

        public void Stop()
        {
            running = false;
            var currentWriter = writer;
            writer = null;
            currentWriter?.Stop();

            var current = client;
            client = null;
            if (current == null)
            {
                return;
            }
            try
            {
                current.Close();
            }
            catch (Exception)
            {
                // teardown is best-effort
            }
        }

        private void OnWriterClosed(int peerId, string reason)
        {
            writer = null;
            var current = client;
            client = null;
            if (current == null)
            {
                return;
            }
            try
            {
                current.Close();
            }
            catch (Exception)
            {
                // teardown is best-effort
            }
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[ReceiveBufferSize];
            var reason = "closed";
            try
            {
                var stream = client!.GetStream();
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
                    inbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, copy));
                }
            }
            catch (Exception e)
            {
                reason = e.GetType().Name;
            }
            finally
            {
                // Release the writer, or its thread waits forever on a queue
                // nothing will feed again.
                writer?.EnqueueClose(reason);
                if (running)
                {
                    inbox.Post(new PeerDisconnectedEvent(ClientSession.HostConnectionId, reason));
                }
                running = false;
            }
        }
    }
}
