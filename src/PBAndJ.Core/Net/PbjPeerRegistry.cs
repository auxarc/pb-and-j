using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>One connected client.</summary>
    public sealed class PbjPeer
    {
        public PbjPeer(int peerId, string name)
        {
            PeerId = peerId;
            Name = name;
        }

        public int PeerId { get; }
        public string Name { get; }
    }

    /// <summary>
    /// The host's roster of connected clients: id allocation, name uniqueness
    /// and capacity. The host itself is not a member — it is always peer 0.
    /// </summary>
    /// <remarks>
    /// This is where a stranger's <see cref="HelloMessage"/> gets judged, which
    /// is why <see cref="Add"/> returns a <see cref="RejectReason"/> rather than
    /// throwing: a bad name is a normal protocol outcome, not an error.
    /// </remarks>
    public sealed class PbjPeerRegistry
    {
        /// <summary>Reserved for the host.</summary>
        public const int HostPeerId = 0;

        /// <summary>Longest accepted player name.</summary>
        public const int MaxNameLength = 32;

        private readonly List<PbjPeer> peers = new List<PbjPeer>();
        private readonly int maxPeers;
        private int nextPeerId = HostPeerId + 1;

        public PbjPeerRegistry(int maxPeers)
        {
            if (maxPeers <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPeers));
            }
            this.maxPeers = maxPeers;
        }

        /// <summary>Connected clients, ordered by id.</summary>
        public IReadOnlyList<PbjPeer> Peers => peers;

        public int Count => peers.Count;

        /// <summary>
        /// Registers a peer. Returns null on success, or why it was refused.
        /// Ids are never reused, so a reconnecting player is a new peer.
        /// </summary>
        public RejectReason? Add(string? name, out PbjPeer? peer)
        {
            peer = null;

            if (string.IsNullOrWhiteSpace(name) || name!.Length > MaxNameLength)
            {
                return RejectReason.InvalidName;
            }
            if (peers.Count >= maxPeers)
            {
                return RejectReason.SessionFull;
            }
            for (var i = 0; i < peers.Count; i++)
            {
                if (string.Equals(peers[i].Name, name, StringComparison.Ordinal))
                {
                    return RejectReason.DuplicateName;
                }
            }

            peer = new PbjPeer(nextPeerId++, name);
            peers.Add(peer);
            return null;
        }

        public bool Remove(int peerId, out PbjPeer? removed)
        {
            for (var i = 0; i < peers.Count; i++)
            {
                if (peers[i].PeerId == peerId)
                {
                    removed = peers[i];
                    peers.RemoveAt(i);
                    return true;
                }
            }
            removed = null;
            return false;
        }

        public bool TryGet(int peerId, out PbjPeer? peer)
        {
            for (var i = 0; i < peers.Count; i++)
            {
                if (peers[i].PeerId == peerId)
                {
                    peer = peers[i];
                    return true;
                }
            }
            peer = null;
            return false;
        }
    }
}
