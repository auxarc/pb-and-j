using System;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjPeerRegistryTests
    {
        private static PbjPeerRegistry Registry(int maxPeers = 3) => new PbjPeerRegistry(maxPeers);

        [Fact]
        public void Add_UsesTheTransportSuppliedId()
        {
            // Connection id and peer id are one id space, so a socket is always
            // addressable by the id that shows up in the logs.
            Assert.Null(Registry().Add(7, "ally", out var peer));
            Assert.Equal(7, peer!.PeerId);
            Assert.Equal("ally", peer.Name);
        }

        [Fact]
        public void Add_WithHostReservedId_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Registry().Add(PbjPeerRegistry.HostPeerId, "x", out _));
            Assert.Equal("peerId", ex.ParamName);
        }

        [Fact]
        public void Add_WithAlreadyRegisteredId_Throws()
        {
            // A duplicate id means the transport misbehaved — our bug, not the
            // peer's, so it is not a RejectReason.
            var registry = Registry();
            registry.Add(1, "a", out _);
            Assert.Throws<InvalidOperationException>(() => registry.Add(1, "b", out _));
        }

        [Fact]
        public void Add_WithDuplicateName_ReturnsDuplicateName()
        {
            var registry = Registry();
            registry.Add(1, "ally", out _);
            Assert.Equal(RejectReason.DuplicateName, registry.Add(2, "ally", out var peer));
            Assert.Null(peer);
        }

        [Fact]
        public void Add_WithNameDifferingOnlyByCase_IsAllowed()
        {
            // Ordinal comparison: "Ally" and "ally" are distinct peers.
            var registry = Registry();
            registry.Add(1, "ally", out _);
            Assert.Null(registry.Add(2, "Ally", out _));
        }

        [Fact]
        public void Add_WhenAtCapacity_ReturnsSessionFull()
        {
            var registry = Registry(maxPeers: 2);
            registry.Add(1, "a", out _);
            registry.Add(2, "b", out _);
            Assert.Equal(RejectReason.SessionFull, registry.Add(3, "c", out var peer));
            Assert.Null(peer);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Add_WithBlankName_ReturnsInvalidName(string? name)
        {
            Assert.Equal(RejectReason.InvalidName, Registry().Add(1, name, out var peer));
            Assert.Null(peer);
        }

        [Fact]
        public void Add_WithNameExceedingMaxLength_ReturnsInvalidName()
        {
            var tooLong = new string('x', PbjPeerRegistry.MaxNameLength + 1);
            Assert.Equal(RejectReason.InvalidName, Registry().Add(1, tooLong, out _));
        }

        [Fact]
        public void Add_WithNameAtMaxLength_IsAccepted()
        {
            var atLimit = new string('x', PbjPeerRegistry.MaxNameLength);
            Assert.Null(Registry().Add(1, atLimit, out _));
        }

        [Fact]
        public void Remove_KnownPeer_ReturnsTrueAndDropsIt()
        {
            var registry = Registry();
            registry.Add(1, "ally", out _);
            Assert.True(registry.Remove(1, out var removed));
            Assert.Equal("ally", removed!.Name);
            Assert.Empty(registry.Peers);
        }

        [Fact]
        public void Remove_UnknownPeer_ReturnsFalse()
        {
            Assert.False(Registry().Remove(99, out var removed));
            Assert.Null(removed);
        }

        [Fact]
        public void Remove_UnknownPeer_WithOthersRegistered_LeavesThemIntact()
        {
            var registry = Registry();
            registry.Add(1, "a", out _);
            registry.Add(2, "b", out _);
            Assert.False(registry.Remove(99, out var removed));
            Assert.Null(removed);
            Assert.Equal(2, registry.Count);
        }

        [Fact]
        public void Remove_FreesCapacity()
        {
            var registry = Registry(maxPeers: 1);
            registry.Add(1, "a", out _);
            Assert.Equal(RejectReason.SessionFull, registry.Add(2, "b", out _));
            registry.Remove(1, out _);
            Assert.Null(registry.Add(3, "c", out _));
        }

        [Fact]
        public void Remove_FreesTheName()
        {
            var registry = Registry();
            registry.Add(1, "ally", out _);
            registry.Remove(1, out _);
            Assert.Null(registry.Add(2, "ally", out _));
        }

        [Fact]
        public void Remove_FreesTheId()
        {
            var registry = Registry();
            registry.Add(1, "a", out _);
            registry.Remove(1, out _);
            Assert.Null(registry.Add(1, "b", out _));
        }

        [Fact]
        public void TryGet_KnownPeer_ReturnsIt()
        {
            var registry = Registry();
            registry.Add(4, "ally", out _);
            Assert.True(registry.TryGet(4, out var found));
            Assert.Equal("ally", found!.Name);
        }

        [Fact]
        public void TryGet_UnknownPeer_ReturnsFalse()
        {
            Assert.False(Registry().TryGet(42, out var found));
            Assert.Null(found);
        }

        [Fact]
        public void TryGet_UnknownPeer_WithOthersRegistered_ReturnsFalse()
        {
            var registry = Registry();
            registry.Add(1, "a", out _);
            registry.Add(2, "b", out _);
            Assert.False(registry.TryGet(42, out var found));
            Assert.Null(found);
        }

        [Fact]
        public void Peers_AreOrderedById_RegardlessOfArrivalOrder()
        {
            var registry = Registry(maxPeers: 4);
            registry.Add(3, "c", out _);
            registry.Add(1, "a", out _);
            registry.Add(2, "b", out _);
            Assert.Equal(new[] { 1, 2, 3 }, registry.Peers.Select(p => p.PeerId).ToArray());
        }

        [Fact]
        public void Peers_InitiallyEmpty()
        {
            Assert.Empty(Registry().Peers);
        }

        [Fact]
        public void Count_ReflectsRegisteredPeers()
        {
            var registry = Registry();
            Assert.Equal(0, registry.Count);
            registry.Add(1, "a", out _);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void Constructor_WithNonPositiveCapacity_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PbjPeerRegistry(0));
            Assert.Equal("maxPeers", ex.ParamName);
        }

        [Fact]
        public void Peer_RetainsFields()
        {
            var peer = new PbjPeer(5, "someone");
            Assert.Equal(5, peer.PeerId);
            Assert.Equal("someone", peer.Name);
        }
    }
}
