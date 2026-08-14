using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>
    /// The connect screen's words. A second register from <see cref="NetLog"/>:
    /// that writes for whoever is reading Player.log, this writes for whoever is
    /// looking at the screen and has never heard of an enum.
    /// </summary>
    public class ConnectTextTests
    {
        // --- refusals ---

        [Fact]
        public void DescribeRejection_ForEveryReason_ProducesADistinctSentence()
        {
            // Iterated rather than listed so a reason added later cannot quietly
            // fall through to a default that says nothing useful.
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            foreach (RejectReason reason in Enum.GetValues(typeof(RejectReason)))
            {
                var sentence = ConnectText.DescribeRejection(reason);

                Assert.False(string.IsNullOrWhiteSpace(sentence));
                Assert.True(seen.Add(sentence), "two reasons share a sentence: " + reason);
            }
        }

        [Fact]
        public void DescribeRejection_NeverShowsTheEnumName()
        {
            // NetLog.HandshakeRejected renders "BadPassphrase" verbatim, which is
            // right for a log line and useless on a screen.
            foreach (RejectReason reason in Enum.GetValues(typeof(RejectReason)))
            {
                Assert.DoesNotContain(reason.ToString(), ConnectText.DescribeRejection(reason), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void DescribeRejection_ForABadPassphrase_SaysWhatToDoNext()
        {
            var sentence = ConnectText.DescribeRejection(RejectReason.BadPassphrase);
            Assert.Contains("passphrase", sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DescribeRejection_ForAModVersionMismatch_SaysBothMachinesNeedTheSameBuild()
        {
            // The fix is not "try again", it is "one of you updates" — which is
            // exactly what the update check then offers to help with.
            var sentence = ConnectText.DescribeRejection(RejectReason.ModVersionMismatch);
            Assert.Contains("same", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("try again", sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DescribeRejection_ForAGameBuildMismatch_NamesTheGameNotTheMod()
        {
            var sentence = ConnectText.DescribeRejection(RejectReason.GameBuildMismatch);
            Assert.Contains("Phantom Brigade", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeRejection_ForAnUnrecognisedValue_StillSaysSomething()
        {
            // A host on a newer build could send a reason this one has no name
            // for. "Refused" with no explanation beats an empty label.
            var sentence = ConnectText.DescribeRejection((RejectReason)200);
            Assert.False(string.IsNullOrWhiteSpace(sentence));
        }

        // --- form problems ---

        [Fact]
        public void DescribeProblem_ForEveryProblem_ProducesADistinctSentence()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            foreach (ConnectProblem problem in Enum.GetValues(typeof(ConnectProblem)))
            {
                var sentence = ConnectText.DescribeProblem(problem);

                if (problem == ConnectProblem.None)
                {
                    // Nothing is wrong, so there is nothing to say.
                    Assert.Equal(string.Empty, sentence);
                    continue;
                }

                Assert.False(string.IsNullOrWhiteSpace(sentence));
                Assert.True(seen.Add(sentence), "two problems share a sentence: " + problem);
            }
        }

        [Fact]
        public void DescribeProblem_ForAnOpenBindWithNoPassphrase_ExplainsTheRiskNotJustTheRule()
        {
            var sentence = ConnectText.DescribeProblem(ConnectProblem.OpenBindNeedsPassphrase);
            Assert.Contains("passphrase", sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DescribeProblem_ForARunningSession_PointsAtLeaveRatherThanAtTheFields()
        {
            // The only actionable thing on the screen in that state is the
            // Leave button, so the sentence has to name it. "A session is
            // already running" on its own is a status report, not direction.
            var sentence = ConnectText.DescribeProblem(ConnectProblem.SessionAlreadyRunning);
            Assert.Contains(ConnectText.LeaveButton(), sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeProblem_ForAnUnrecognisedValue_StillSaysSomething()
        {
            Assert.False(string.IsNullOrWhiteSpace(ConnectText.DescribeProblem((ConnectProblem)200)));
        }

        // --- the passphrase warning ---

        [Fact]
        public void RememberWarning_SaysPlainTextOutLoud()
        {
            // Somebody ticking "remember" has agreed to convenience, not to
            // having their shared secret written to disk unencrypted. If the
            // screen does not say it, they have not been told.
            var warning = ConnectText.RememberWarning();
            Assert.Contains("not encrypted", warning, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RememberLabel_LabelsAndDoesNotAlsoWarn()
        {
            // One element, one job: the tickbox says what it does, the warning
            // beside it says what it costs.
            var label = ConnectText.RememberLabel();
            Assert.DoesNotContain("encrypt", label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("warning", label, StringComparison.OrdinalIgnoreCase);
        }

        // --- captions carry their own hints ---

        [Fact]
        public void AddressLabel_HintsAtBothUsesOfTheField()
        {
            // One field serves two jobs: the host to reach when joining, and the
            // interface to bind when hosting. NGUI gives us no placeholder to
            // explain that — it renders a fixed em-dash for an empty field — so
            // the caption has to carry it.
            Assert.Contains("0.0.0.0", ConnectText.AddressLabel(), StringComparison.Ordinal);
        }

        [Fact]
        public void PassphraseLabel_SaysWhenItCanBeLeftEmpty()
        {
            Assert.Contains("local", ConnectText.PassphraseLabel(), StringComparison.OrdinalIgnoreCase);
        }

        // --- nothing remembered yet ---

        [Fact]
        public void NothingRemembered_TellsThemHowToSetItRatherThanJustSayingItIsEmpty()
        {
            // An empty state is an invitation to act. Naming the command is the
            // difference between a dead end and a first step.
            var text = ConnectText.NothingRemembered();
            Assert.Contains("pbj.join", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[pb-and-j]", text, StringComparison.Ordinal);
        }

        [Fact]
        public void ConfirmJoin_NamesTheTargetSoNobodyConnectsSomewhereBySurprise()
        {
            var text = ConnectText.ConfirmJoin("friend.example.com", 27600);
            Assert.Contains("friend.example.com:27600", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[pb-and-j]", text, StringComparison.Ordinal);
        }

        // --- borrowing the log's lines for the screen ---

        [Fact]
        public void ForScreen_StripsTheLogPrefixSoALabelDoesNotShowIt()
        {
            // NetGlue.Host and Join already return good sentences; they just
            // arrive wearing the log's marker.
            Assert.Equal(
                "host listening on 0.0.0.0:27600",
                ConnectText.ForScreen("[pb-and-j] host listening on 0.0.0.0:27600"));
        }

        [Fact]
        public void ForScreen_OnALineWithoutThePrefix_LeavesItAlone()
        {
            Assert.Equal("already connected", ConnectText.ForScreen("already connected"));
        }

        [Fact]
        public void ForScreen_OnNullOrEmpty_GivesEmptyRatherThanThrowing()
        {
            Assert.Equal(string.Empty, ConnectText.ForScreen(null));
            Assert.Equal(string.Empty, ConnectText.ForScreen(""));
        }

        [Fact]
        public void ForScreen_StripsOnlyTheLeadingPrefix()
        {
            // A line quoting the marker mid-sentence keeps it.
            Assert.Equal(
                "see [pb-and-j] in the log",
                ConnectText.ForScreen("[pb-and-j] see [pb-and-j] in the log"));
        }

        // --- action names, kept stable across the flow ---

        [Fact]
        public void ActionNames_AreVerbsThatMatchTheirStatusLines()
        {
            // The button that says Host produces a status that says Hosting. An
            // interface where the action changes name between press and result
            // is one the player has to re-learn each time.
            Assert.Equal("Host", ConnectText.HostButton());
            Assert.Equal("Join", ConnectText.JoinButton());
            Assert.Equal("Leave", ConnectText.LeaveButton());

            // Named for the room it opens, matching LobbyText.Title(). A button
            // and the screen it opens disagreeing about their own name is how a
            // player loses track of where Back goes.
            Assert.Equal("Lobby", ConnectText.LobbyButton());
            Assert.StartsWith("Hosting", ConnectText.Hosting("0.0.0.0", 27600));
            Assert.StartsWith("Joining", ConnectText.Joining("friend.example.com", 27600));
        }

        [Fact]
        public void StatusLines_NameTheAddressAndPortSoTheyCanBeReadBackOverVoice()
        {
            Assert.Contains("0.0.0.0:27600", ConnectText.Hosting("0.0.0.0", 27600));
            Assert.Contains("friend.example.com:27600", ConnectText.Joining("friend.example.com", 27600));
        }

        [Fact]
        public void FieldLabels_AreSentenceCaseAndNotShouting()
        {
            foreach (var label in new[]
                     {
                         ConnectText.AddressLabel(), ConnectText.PortLabel(),
                         ConnectText.PassphraseLabel(), ConnectText.RememberLabel(),
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(label));
                Assert.NotEqual(label.ToUpperInvariant(), label);
            }
        }

        [Fact]
        public void NoScreenTextCarriesTheLogPrefix()
        {
            foreach (var text in new[]
                     {
                         ConnectText.AddressLabel(), ConnectText.PortLabel(),
                         ConnectText.PassphraseLabel(), ConnectText.RememberLabel(),
                         ConnectText.RememberWarning(), ConnectText.Title(),
                         ConnectText.HostButton(), ConnectText.JoinButton(),
                         ConnectText.LeaveButton(), ConnectText.LobbyButton(),
                         ConnectText.Hosting("0.0.0.0", 1), ConnectText.Joining("x", 1),
                         ConnectText.DescribeRejection(RejectReason.BadPassphrase),
                         ConnectText.DescribeProblem(ConnectProblem.AddressEmpty),
                     })
            {
                Assert.DoesNotContain("[pb-and-j]", text, StringComparison.Ordinal);
            }
        }
    }
}
