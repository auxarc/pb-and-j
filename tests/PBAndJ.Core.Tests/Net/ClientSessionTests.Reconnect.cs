using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Reconnecting, and the snapshot correction that follows it: the host's
    // snapshot is authoritative, and this is where a client is overruled.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- reconnect ---

        [Fact]
        public void Start_WithAResumeToken_SendsRejoinRatherThanHello()
        {
            var client = new ClientSession("ally", "0.2.0", bridge, "7f3a91", 1, "tok");
            var rejoin = Assert.IsType<RejoinMessage>(Single<SendEffect>(client.Start()).Message);

            Assert.Equal(PbjProtocol.Magic, rejoin.Magic);
            Assert.Equal(PbjProtocol.Version, rejoin.ProtocolVersion);
            Assert.Equal("ally", rejoin.PlayerName);
            Assert.Equal("7f3a91", rejoin.SessionId);
            Assert.Equal(1, rejoin.ClaimedPeerId);
            Assert.Equal("tok", rejoin.ResumeToken);
        }

        [Fact]
        public void Start_WithNoResumeToken_SendsHello()
        {
            Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
        }

        [Fact]
        public void Welcome_StoresTheResumeTokenForALaterReturn()
        {
            Assert.Equal("tok", Welcomed().ResumeToken);
        }

        // --- snapshot correction ---

        private static UnitSnapshot Snap(string name, float x = 1f) =>
            new UnitSnapshot(name, new Vec3(x, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f);

        [Fact]
        public void Snapshot_ClearsStaleLocalOrdersBeforeApplying()
        {
            // A client's planned orders never execute, so by turn 3 its timeline
            // is junk and CaptureLocalOrders would re-send orders already run.
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })).ToList();

            var clearAt = effects.FindIndex(e => e is ClearLocalOrdersEffect);
            var applyAt = effects.FindIndex(e => e is ApplySnapshotEffect);
            Assert.True(clearAt >= 0 && applyAt > clearAt);
        }

        [Fact]
        public void Snapshot_CarriesTheHostsDigestOnTheEffect()
        {
            var client = Welcomed();
            var apply = Single<ApplySnapshotEffect>(client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })));

            Assert.Equal(3, apply.Turn);
            Assert.Equal("abc", apply.ExpectedDigest);
            Assert.Equal("unit_b", Assert.Single(apply.Units).Name);
        }
    }
}
