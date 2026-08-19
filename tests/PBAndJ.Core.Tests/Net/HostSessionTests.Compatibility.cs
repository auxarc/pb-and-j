using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Build compatibility and the handshake deadline (M7). One section of the original.
    // Guarded, Hello and RejectedBy are used only here -- 11, 9 and 4 times, nowhere else.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- build compatibility and the handshake deadline (M7) ---

        private HostSession Guarded(string? passphrase = null) =>
            new HostSession("host", "7f3a91", 3, bridge, "secret",
                new SessionRequirements("0.2.0", "b8339", passphrase));

        private static HelloMessage Hello(
            string mod = "0.2.0", string? build = "b8339", string? passphrase = null, string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, mod, name, build, passphrase);

        private static RejectMessage RejectedBy(IEnumerable<PbjEffect> effects) =>
            (RejectMessage)effects.OfType<SendEffect>().Single(s => s.Message is RejectMessage).Message;

        [Fact]
        public void Hello_WithAMatchingBuild_IsWelcomed()
        {
            var effects = Guarded().HandleMessage(1, Hello());
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        // Without this a friend on a different mod build connects perfectly and
        // then diverges on every turn, which reads as a netcode bug.
        [Fact]
        public void Hello_WithADifferentModVersion_IsRejectedAndDisconnected()
        {
            var host = Guarded();
            var effects = host.HandleMessage(1, Hello(mod: "0.1.0"));

            Assert.Equal(RejectReason.ModVersionMismatch, RejectedBy(effects).Reason);
            Assert.Single(All<DisconnectEffect>(effects));
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void Hello_WithADifferentGameBuild_IsRejected()
        {
            var effects = Guarded().HandleMessage(1, Hello(build: "b0001"));
            Assert.Equal(RejectReason.GameBuildMismatch, RejectedBy(effects).Reason);
        }

        // The standalone harness is a legitimate peer with no game to report.
        [Fact]
        public void Hello_WithNoGameBuild_IsAccepted()
        {
            var effects = Guarded().HandleMessage(1, Hello(build: null));
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        [Fact]
        public void Hello_WithoutTheRequiredPassphrase_IsRejected()
        {
            var effects = Guarded("hunter2").HandleMessage(1, Hello(passphrase: "wrong"));
            Assert.Equal(RejectReason.BadPassphrase, RejectedBy(effects).Reason);
        }

        [Fact]
        public void Hello_WithTheRequiredPassphrase_IsWelcomed()
        {
            var effects = Guarded("hunter2").HandleMessage(1, Hello(passphrase: "hunter2"));
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        // A returning peer is as unauthenticated as a new one: the resume token
        // proves which departure this is, not that the sender belongs here.
        [Fact]
        public void Rejoin_WithoutTheRequiredPassphrase_IsRejected()
        {
            var host = Guarded("hunter2");
            var token = ((WelcomeMessage)All<SendEffect>(host.HandleMessage(1, Hello(passphrase: "hunter2")))
                .Single(s => s.Message is WelcomeMessage).Message).ResumeToken;
            host.Handle(new PeerDisconnectedEvent(1, "dropped"));

            var effects = host.HandleMessage(2, new RejoinMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", "ally", "7f3a91", 1, token, "b8339", "wrong"));

            Assert.Equal(RejectReason.BadPassphrase, RejectedBy(effects).Reason);
        }

        [Fact]
        public void ASocketThatNeverSaysHello_IsDroppedAfterTheHandshakeDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));

            var effects = host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("never handshook"));
        }

        [Fact]
        public void ASocketWithinTheHandshakeDeadline_IsLeftAlone()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds - 1))));
        }

        [Fact]
        public void APeerThatHandshook_IsNotDroppedByTheHandshakeDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));
            host.HandleMessage(1, Hello());

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1))));
            Assert.Single(host.Peers);
        }

        // A rejected socket is already being disconnected; the deadline must not
        // queue a second disconnect for it on the next tick.
        [Fact]
        public void ARejectedSocket_IsNotAlsoDroppedByTheDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));
            host.HandleMessage(1, Hello(mod: "0.1.0"));

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1))));
        }

        [Fact]
        // Not an M7 test. It sat at the tail of the M7 section in the original and
        // moved with it; the subject is the snapshot events of .Snapshots.cs.
        public void SnapshotApplied_IsIgnoredOnTheHost()
        {
            Assert.Empty(WithPeer().Handle(new SnapshotAppliedEvent(3, 1, "a", "a")));
        }
    }
}
