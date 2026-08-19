using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Constructing a client and getting it through the handshake: what it sends,
    // what it accepts, and what it refuses.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- construction and handshake ---

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankPlayerName_Throws(string? name)
        {
            var ex = Assert.Throws<ArgumentException>(() => new ClientSession(name!, "v", bridge));
            Assert.Equal("playerName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new ClientSession("ally", "v", null!));
            Assert.Equal("bridge", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullModVersion_IsAccepted()
        {
            var hello = (HelloMessage)Single<SendEffect>(new ClientSession("ally", null!, bridge).Start()).Message;
            Assert.Equal(string.Empty, hello.ModVersion);
        }

        [Fact]
        public void Start_SendsHelloWithProtocolAndName()
        {
            var hello = Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
            Assert.Equal(PbjProtocol.Magic, hello.Magic);
            Assert.Equal(PbjProtocol.Version, hello.ProtocolVersion);
            Assert.Equal("0.2.0", hello.ModVersion);
            Assert.Equal("ally", hello.PlayerName);
        }

        [Fact]
        public void Handle_PeerConnected_SendsHello()
        {
            var effects = Client().Handle(new PeerConnectedEvent(0, "host"));
            Assert.IsType<HelloMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void HandleMessage_Welcome_StoresIdentityAndEntersPlanning()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, Welcome());

            Assert.Equal(1, client.PeerId);
            Assert.Equal("7f3a91", client.SessionId);
            Assert.Equal("host", client.HostName);
            Assert.Equal(3, client.Turn);
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("welcome | peer #1"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2 participants"));
        }

        [Fact]
        public void HandleMessage_Welcome_WhenHostNotInCombat_EntersLobby()
        {
            bridge.InCombat = false;
            Assert.Equal(ClientSessionState.Lobby, Welcomed().State);
        }

        [Fact]
        public void HandleMessage_Welcome_Twice_FaultsAndDisconnects()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, Welcome());
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Theory]
        [InlineData(RejectReason.BadMagic)]
        [InlineData(RejectReason.VersionMismatch)]
        [InlineData(RejectReason.SessionFull)]
        [InlineData(RejectReason.DuplicateName)]
        [InlineData(RejectReason.InvalidName)]
        [InlineData(RejectReason.NotAcceptingPeers)]
        public void HandleMessage_Reject_LogsReasonAndFaults(RejectReason reason)
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new RejectMessage(reason, "nope"));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains(reason.ToString()));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Rejection_AfterARefusal_IsRetainedSoTheScreenCanSayWhichThingWasWrong()
        {
            // The reason used to be logged and dropped. A connect screen that can
            // only say "failed" sends someone to check their firewall when the
            // real answer is that they typed the passphrase wrong.
            var client = Client();
            client.Start();
            client.HandleMessage(0, new RejectMessage(RejectReason.BadPassphrase, "nope"));

            Assert.Equal(RejectReason.BadPassphrase, client.Rejection);
        }

        [Fact]
        public void Rejection_BeforeAnyRefusal_IsNullRatherThanNone()
        {
            // None is a real RejectReason value, so it cannot double as "no
            // refusal has happened" — a screen reading it would announce one.
            var client = Client();
            client.Start();

            Assert.Null(client.Rejection);
        }

        [Fact]
        public void Rejection_AfterAWelcome_StaysNull()
        {
            Assert.Null(Welcomed().Rejection);
        }

        [Fact]
        public void HandleMessage_BeforeWelcome_Faults()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("before Welcome"));
        }

        [Fact]
        public void HandleMessage_WithClientOnlyMessage_Faults()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new ReadyMessage(3, null));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void HandleMessage_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Client().HandleMessage(0, null!));
            Assert.Equal("message", ex.ParamName);
        }
    }
}
