using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public partial class NetLogTests
    {
        // Where to start reading, and the session lifecycle.
        //
        // Unlike the other split test classes, this one has NO shared fixture to
        // put here: all 164 members are tests, there is not a single helper or
        // field in the class. So the primary holds the first section rather than
        // an empty shell, and the eight other parts are:
        //
        //   .Handshake.cs  a socket connecting through to assigned units
        //   .Barrier.cs    ready, stale, resync, commit
        //   .Orders.cs     the front of the orders-and-commit span
        //   .Playback.cs   keyframes, poses, asset tracks
        //   .Commit.cs     the back of it: reconnect, rejections, commit, digests
        //   .Status.cs     what `pbj.status` prints
        //   .Lobby.cs      the lobby (M11a)
        //   .Shipping.cs   shipping the fight (M12b)
        //
        // The boundaries are the author's own `// --- name ---` banners. One had to
        // be divided: `// --- orders and commit ---` runs 667 lines, over the
        // 500-line gate, and is cut at its own seams into .Orders/.Playback/.Commit.
        //
        // Four tests were filed away from their subject in the original. The two
        // HostListeningOpenly tests sat under the orders banner and are now beside
        // HostListening below; HandshakeTimedOut went to .Handshake.cs;
        // MailboxOverflowed went to .Orders.cs, beside the send queue it is the
        // inbound counterpart of; and Lines_AreCultureIndependent had drifted
        // twenty-five tests past its own `// --- culture ---` banner, to the end of
        // the lobby section, leaving that banner heading nothing at all.
        //
        // The culture test samples two lines rather than all of them --
        // HostListening and TurnCommitted, under de-DE -- but those two now live in
        // different parts, so no section file owns it and it belongs here with the
        // banner that names it.
        // --- session lifecycle ---

        [Fact]
        public void HostListening_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] host listening on 127.0.0.1:27600 | protocol v1 | slots 3",
                NetLog.HostListening("127.0.0.1", 27600, 1, 3));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void HostListening_WithBlankAddress_Throws(string? address)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.HostListening(address!, 1, 1, 1));
            Assert.Equal("bindAddress", ex.ParamName);
        }

        [Fact]
        public void HostListeningOpenly_WarnsAboutTheExposure()
        {
            var line = NetLog.HostListeningOpenly("0.0.0.0", 27600);
            Assert.Contains("OPEN LISTENER on 0.0.0.0:27600", line);
            Assert.Contains("in the clear", line);
            Assert.Contains("pbj.net-stop", line);
        }

        [Fact]
        public void HostListeningOpenly_WithNoBindAddress_Throws()
        {
            Assert.Throws<ArgumentException>(() => NetLog.HostListeningOpenly(" ", 1));
        }

        [Fact]
        public void ClientConnecting_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] connecting to 127.0.0.1:27600 as 'ally'",
                NetLog.ClientConnecting("127.0.0.1", 27600, "ally"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ClientConnecting_WithBlankHost_Throws(string? host)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.ClientConnecting(host!, 1, "ally"));
            Assert.Equal("hostAddress", ex.ParamName);
        }

        [Fact]
        public void ClientConnecting_WithBlankName_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.ClientConnecting("h", 1, "  "));
            Assert.Equal("playerName", ex.ParamName);
        }

        [Fact]
        public void SessionClosed_PluralisesPeers()
        {
            Assert.Equal("[pb-and-j] session closed | 0 peers | listener stopped", NetLog.SessionClosed(0));
            Assert.Equal("[pb-and-j] session closed | 1 peer | listener stopped", NetLog.SessionClosed(1));
        }

        [Fact]
        public void PumpFailed_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] networking stopped after an error — NullReferenceException",
                NetLog.PumpFailed("NullReferenceException"));
        }

        [Fact]
        public void PumpFailed_WithBlankDetail_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.PumpFailed(" "));
            Assert.Equal("detail", ex.ParamName);
        }

        [Fact]
        public void TransportFailed_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] transport failed — socket closed", NetLog.TransportFailed("socket closed"));
        }

        [Fact]
        public void TransportFailed_WithBlankDetail_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.TransportFailed(""));
            Assert.Equal("detail", ex.ParamName);
        }

        // --- culture ---

        [Fact]
        public void Lines_AreCultureIndependent()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(
                    "[pb-and-j] host listening on 127.0.0.1:27600 | protocol v1 | slots 3",
                    NetLog.HostListening("127.0.0.1", 27600, 1, 3));
                Assert.Equal("[pb-and-j] turn 1000 committed", NetLog.TurnCommitted(1000));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    
}
}
