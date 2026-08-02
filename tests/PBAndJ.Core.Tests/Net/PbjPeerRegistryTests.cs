using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjPeerRegistryTests
    {
        private static PbjPeerRegistry Registry(int maxPeers = 3) => new PbjPeerRegistry(maxPeers);

        [Fact]
        public void Add_FirstPeer_AssignsIdOne()
        {
            // Id 0 is reserved for the host, always.
            Assert.Null(Registry().Add("ally", out var peer));
            Assert.Equal(1, peer!.PeerId);
            Assert.Equal("ally", peer.Name);
        }

        [Fact]
        public void Add_SecondPeer_AssignsIdTwo()
        {
            var registry = Registry();
            registry.Add("a", out _);
            registry.Add("b", out var second);
            Assert.Equal(2, second!.PeerId);
        }

        [Fact]
        public void Add_AfterRemoval_DoesNotReuseIds()
        {
            // A rejoining player is a new peer; reusing ids would silently
            // inherit the previous peer's assignments and barrier state.
            var registry = Registry();
            registry.Add("a", out var first);
            registry.Remove(first!.PeerId, out _);
            registry.Add("b", out var second);
            Assert.Equal(2, second!.PeerId);
        }

        [Fact]
        public void Add_WithDuplicateName_ReturnsDuplicateName()
        {
            var registry = Registry();
            registry.Add("ally", out _);
            Assert.Equal(RejectReason.DuplicateName, registry.Add("ally", out var peer));
            Assert.Null(peer);
        }

        [Fact]
        public void Add_WithNameDifferingOnlyByCase_IsAllowed()
        {
            // Ordinal comparison: "Ally" and "ally" are distinct peers.
            var registry = Registry();
            registry.Add("ally", out _);
            Assert.Null(registry.Add("Ally", out _));
        }

        [Fact]
        public void Add_WhenAtCapacity_ReturnsSessionFull()
        {
            var registry = Registry(maxPeers: 2);
            registry.Add("a", out _);
            registry.Add("b", out _);
            Assert.Equal(RejectReason.SessionFull, registry.Add("c", out var peer));
            Assert.Null(peer);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Add_WithBlankName_ReturnsInvalidName(string? name)
        {
            Assert.Equal(RejectReason.InvalidName, Registry().Add(name, out var peer));
            Assert.Null(peer);
        }

        [Fact]
        public void Add_WithNameExceedingMaxLength_ReturnsInvalidName()
        {
            var tooLong = new string('x', PbjPeerRegistry.MaxNameLength + 1);
            Assert.Equal(RejectReason.InvalidName, Registry().Add(tooLong, out _));
        }

        [Fact]
        public void Add_WithNameAtMaxLength_IsAccepted()
        {
            var atLimit = new string('x', PbjPeerRegistry.MaxNameLength);
            Assert.Null(Registry().Add(atLimit, out _));
        }

        [Fact]
        public void Remove_KnownPeer_ReturnsTrueAndDropsIt()
        {
            var registry = Registry();
            registry.Add("ally", out var peer);
            Assert.True(registry.Remove(peer!.PeerId, out var removed));
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
            registry.Add("a", out _);
            registry.Add("b", out _);
            Assert.False(registry.Remove(99, out var removed));
            Assert.Null(removed);
            Assert.Equal(2, registry.Count);
        }

        [Fact]
        public void Remove_FreesCapacity()
        {
            var registry = Registry(maxPeers: 1);
            registry.Add("a", out var peer);
            Assert.Equal(RejectReason.SessionFull, registry.Add("b", out _));
            registry.Remove(peer!.PeerId, out _);
            Assert.Null(registry.Add("c", out _));
        }

        [Fact]
        public void Remove_FreesTheName()
        {
            var registry = Registry();
            registry.Add("ally", out var peer);
            registry.Remove(peer!.PeerId, out _);
            Assert.Null(registry.Add("ally", out _));
        }

        [Fact]
        public void TryGet_KnownPeer_ReturnsIt()
        {
            var registry = Registry();
            registry.Add("ally", out var peer);
            Assert.True(registry.TryGet(peer!.PeerId, out var found));
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
            registry.Add("a", out _);
            registry.Add("b", out _);
            Assert.False(registry.TryGet(42, out var found));
            Assert.Null(found);
        }

        [Fact]
        public void Peers_AreOrderedById()
        {
            var registry = Registry(maxPeers: 4);
            registry.Add("c", out _);
            registry.Add("a", out _);
            registry.Add("b", out _);
            Assert.Equal(new[] { 1, 2, 3 }, System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(registry.Peers, p => p.PeerId)));
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
            registry.Add("a", out _);
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
