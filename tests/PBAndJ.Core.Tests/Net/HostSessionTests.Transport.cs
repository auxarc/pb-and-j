using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Transport failure and teardown. One section of the original, including the nested
    // UnsupportedEvent class, whose single use is a test in this file.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- transport failure and teardown ---

        [Fact]
        public void Handle_TransportFailed_ClosesSessionAndUnlocks()
        {
            var host = WithPeer();
            var effects = host.Handle(new TransportFailedEvent("listener died"));
            Assert.Equal(HostSessionState.Closed, host.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("listener died"));
        }

        [Fact]
        public void Handle_TransportFailed_WithNoReason_StillLogs()
        {
            Assert.Contains(All<LogEffect>(Host().Handle(new TransportFailedEvent(null))),
                l => l.Line.Contains("unknown"));
        }

        [Fact]
        public void Handle_AfterClosed_ProducesNoEffects()
        {
            var host = Host();
            host.Handle(new TransportFailedEvent("x"));
            Assert.Empty(host.Handle(new LocalReadyEvent()));
            Assert.Empty(host.HandleMessage(1, GoodHello()));
        }

        [Fact]
        public void Handle_TransportLog_ForwardsTheLine()
        {
            Assert.Equal("accepted 127.0.0.1:1",
                Single<LogEffect>(Host().Handle(new TransportLogEvent("accepted 127.0.0.1:1"))).Line);
        }

        [Fact]
        public void Handle_TransportLog_WithNoLine_LogsPlaceholder()
        {
            Assert.Equal("unknown", Single<LogEffect>(Host().Handle(new TransportLogEvent(null))).Line);
        }

        [Fact]
        public void Handle_PeerBytes_ProducesNoEffects()
        {
            // Raw bytes are decoded by the runtime and arrive via HandleMessage.
            Assert.Empty(Host().Handle(new PeerBytesEvent(1, new byte[] { 1 })));
        }

        [Fact]
        public void Handle_WithNullEvent_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Host().Handle(null!));
            Assert.Equal("evt", ex.ParamName);
        }

        [Fact]
        public void Handle_WithUnsupportedEventKind_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => Host().Handle(new UnsupportedEvent()));
        }

        private sealed class UnsupportedEvent : PbjInboundEvent
        {
            public override PbjInboundEventKind Kind => (PbjInboundEventKind)200;
        }
    }
}
