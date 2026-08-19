using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The lobby (M11a): state, ready, unready, load and loaded -- the exact bytes
    // for the three the screen sends most, every field of the state message, the
    // unknown-outcome path that decodes rather than throwing, and the peer cap.
    // The file then ends with EncodedMessage_SurvivesFramingRoundTrip, which is
    // not lobby at all: it is the one test that composes the codec with the
    // framing layer, and it is here because it was last in the original file,
    // inside this banner.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        // --- lobby (M11a) ---

        [Fact]
        public void Encode_LobbyState_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new LobbyStateMessage(2, "s", "ab", new[]
            {
                new LobbyPeerState(0, "h", true),
            }));

            var expected = new byte[]
            {
                0x17,                               // type LobbyState (23)
                0x02, 0x00, 0x00, 0x00,             // selectionVersion 2
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveKey "s"
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // saveDigest "ab"
                0x01, 0x00, 0x00, 0x00,             // one peer
                0x00, 0x00, 0x00, 0x00,             // peerId 0
                0x01, 0x00, 0x00, 0x00, 0x68,       // name "h"
                0x01,                               // ready
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void Encode_LobbyReady_ProducesExactBytes()
        {
            Assert.Equal(
                new byte[] { 0x18, 0x02, 0x00, 0x00, 0x00 },
                PbjMessageCodec.Encode(new LobbyReadyMessage(2)));
        }

        [Fact]
        public void Encode_LobbyUnready_ProducesExactBytes()
        {
            Assert.Equal(
                new byte[] { 0x19, 0x02, 0x00, 0x00, 0x00 },
                PbjMessageCodec.Encode(new LobbyUnreadyMessage(2)));
        }

        [Fact]
        public void RoundTrip_LobbyState_PreservesEveryField()
        {
            var m = RoundTrip(new LobbyStateMessage(4, "pbj_campaign", "3f9c1a04", new[]
            {
                new LobbyPeerState(0, "host", true),
                new LobbyPeerState(1, "ally", false),
            }));

            Assert.Equal(4, m.SelectionVersion);
            Assert.Equal("pbj_campaign", m.SaveKey);
            Assert.Equal("3f9c1a04", m.SaveDigest);
            Assert.Equal(2, m.Peers.Count);
            Assert.Equal(0, m.Peers[0].PeerId);
            Assert.Equal("host", m.Peers[0].Name);
            Assert.True(m.Peers[0].Ready);
            Assert.Equal("ally", m.Peers[1].Name);
            Assert.False(m.Peers[1].Ready);
        }

        [Fact]
        public void RoundTrip_LobbyState_WithNothingSelected_KeepsTheNulls()
        {
            // "No save chosen yet" is a real lobby state, not a malformed one.
            var m = RoundTrip(new LobbyStateMessage(0, null, null, null));
            Assert.Equal(0, m.SelectionVersion);
            Assert.Null(m.SaveKey);
            Assert.Null(m.SaveDigest);
            Assert.Empty(m.Peers);
        }

        [Fact]
        public void RoundTrip_LobbyReadyAndUnready_PreserveTheSelection()
        {
            Assert.Equal(9, RoundTrip(new LobbyReadyMessage(9)).SelectionVersion);
        }

        [Fact]
        public void LobbyLoad_CarriesTheSelectionAndTheSave()
        {
            var round = RoundTrip(new LobbyLoadMessage(4, "pbj_campaign", "abc123"));
            Assert.Equal(4, round.SelectionVersion);
            Assert.Equal("pbj_campaign", round.SaveKey);
            Assert.Equal("abc123", round.SaveDigest);
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void LobbyLoaded_CarriesEveryOutcome(LoadOutcome outcome)
        {
            var round = RoundTrip(new LobbyLoadedMessage(4, outcome));
            Assert.Equal(4, round.SelectionVersion);
            Assert.Equal(outcome, round.Outcome);
        }

        [Fact]
        public void LobbyLoaded_WithAnOutcomeWeDoNotKnow_DecodesRatherThanThrowing()
        {
            // The cast is unvalidated, exactly as RejectReason's is. A peer can
            // put any byte on the wire and the host must survive reading it —
            // faulting the session over an unknown enum value would let a peer
            // hang up on us by sending one.
            var round = RoundTrip(new LobbyLoadedMessage(1, (LoadOutcome)200));
            Assert.Equal((LoadOutcome)200, round.Outcome);
            Assert.Equal(9, RoundTrip(new LobbyUnreadyMessage(9)).SelectionVersion);
        }

        [Fact]
        public void Decode_LobbyStateOverThePeerCap_Throws()
        {
            // The roster shares Welcome's cap, since it is the same roster.
            var peers = new LobbyPeerState[PbjMessageCodec.MaxPeersPerWelcome + 1];
            for (var i = 0; i < peers.Length; i++)
            {
                peers[i] = new LobbyPeerState(i, "p" + i, false);
            }
            var encoded = PbjMessageCodec.Encode(new LobbyStateMessage(1, "s", null, peers));
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(encoded));
        }

        [Fact]
        public void EncodedMessage_SurvivesFramingRoundTrip()
        {
            // The two layers compose: framing carries whole encoded messages.
            var encoded = PbjMessageCodec.Encode(new TurnCommitMessage(42));
            var decoder = new FrameDecoder(4096);
            var frames = decoder.Feed(FrameEncoder.Encode(encoded), 0, FrameEncoder.HeaderLength + encoded.Length);
            Assert.Single(frames);
            Assert.Equal(42, Assert.IsType<TurnCommitMessage>(PbjMessageCodec.Decode(frames[0])).Turn);
        }
    }
}
