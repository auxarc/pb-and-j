using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>
    /// The connect screen's validation. Everything arrives as a string, because
    /// that is what a text field yields, so the parsing is part of the rule.
    /// </summary>
    /// <remarks>
    /// These rules are the same ones <c>pbj.host</c> enforces — not a copy of
    /// them. They used to live in <c>NetGlue.Host</c>, which is excluded from
    /// coverage, so a real decision sat outside the gate. Two copies, one tested
    /// and one not, would drift the first time either changed.
    /// </remarks>
    public class ConnectFormTests
    {
        // --- joining: a friend has a hostname, not an address ---

        [Fact]
        public void CheckJoin_ForADnsName_IsFine_BecauseFriendsHaveHostnamesNotAddresses()
        {
            // pbj.join has never parsed its address — TcpClient.Connect resolves
            // names. Requiring an IP here would break a path that works today.
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckJoin("friend.example.com", "27600"));
        }

        [Fact]
        public void CheckJoin_ForAnIpAddress_IsAlsoFine()
        {
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckJoin("10.0.0.5", "27600"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void CheckJoin_WithNoAddress_SaysSoRatherThanFiringIntoNothing(string? address)
        {
            Assert.Equal(ConnectProblem.AddressEmpty, ConnectRules.CheckJoin(address, "27600"));
        }

        [Theory]
        [InlineData("http://friend.example.com")]      // a pasted URL
        [InlineData("friend.example.com/pb")]          // a path
        [InlineData("friend example com")]             // spaces
        [InlineData("10.0.0.5:27600")]                 // port smuggled into the address
        public void CheckJoin_ForSomethingThatIsNotAHost_SaysTheAddressIsWrong(string address)
        {
            // Deliberately structural, not a hostname grammar: the job is to
            // catch a paste, not to out-guess the resolver.
            Assert.Equal(ConnectProblem.AddressMalformed, ConnectRules.CheckJoin(address, "27600"));
        }

        // --- hosting: a bind is an interface, and an open one needs a door ---

        [Fact]
        public void CheckHost_ForALoopbackBind_NeedsNoPassphrase()
        {
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckHost("127.0.0.1", "27600", null));
        }

        [Fact]
        public void CheckHost_ForADnsName_SaysTheBindMustBeAnAddress()
        {
            // A bind names an interface on this machine. "localhost" is a name to
            // resolve, not an interface, and pbj.host has always required an IP.
            Assert.Equal(ConnectProblem.BindNotAnIpAddress, ConnectRules.CheckHost("localhost", "27600", null));
        }

        [Fact]
        public void CheckHost_ForAnOpenBindWithNoPassphrase_RefusesToListenSilently()
        {
            // The protocol is open source and an accepted peer can submit orders
            // for the units it is dealt. A listener on a routable address with no
            // passphrase is joinable by anything that finds the port.
            Assert.Equal(
                ConnectProblem.OpenBindNeedsPassphrase,
                ConnectRules.CheckHost("0.0.0.0", "27600", null));
        }

        [Fact]
        public void CheckHost_ForAnOpenBindWithAPassphrase_IsAllowed()
        {
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckHost("0.0.0.0", "27600", "hunter2"));
        }

        [Fact]
        public void CheckHost_TreatsAWhitespacePassphraseAsAbsent()
        {
            Assert.Equal(
                ConnectProblem.OpenBindNeedsPassphrase,
                ConnectRules.CheckHost("0.0.0.0", "27600", "   "));
        }

        [Fact]
        public void CheckHostBind_IsTheSameRuleWithoutThePortField()
        {
            // The console path already has an int port, so it asks only about the
            // half it shares with the screen.
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckHostBind("127.0.0.1", null));
            Assert.Equal(ConnectProblem.BindNotAnIpAddress, ConnectRules.CheckHostBind("nope", null));
            Assert.Equal(ConnectProblem.OpenBindNeedsPassphrase, ConnectRules.CheckHostBind("0.0.0.0", ""));
        }

        // --- ports ---

        [Theory]
        [InlineData("-1")]
        [InlineData("+27600")]
        [InlineData(" 27600 ")]
        [InlineData("27600.0")]
        [InlineData("wat")]
        [InlineData("")]
        public void CheckJoin_ForAPortThatIsNotPlainDigits_RefusesIt(string port)
        {
            // int.TryParse alone accepts a sign and surrounding space — the same
            // trap ModVersion.TryPart already documents.
            Assert.Equal(ConnectProblem.PortUnreadable, ConnectRules.CheckJoin("10.0.0.5", port));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("65536")]
        [InlineData("99999")]
        public void CheckJoin_ForAPortOutsideTheUsableRange_RefusesIt(string port)
        {
            // Port 0 would ask the OS for an arbitrary free port: it binds fine
            // and is then unreachable by the person we just told to connect.
            Assert.Equal(ConnectProblem.PortOutOfRange, ConnectRules.CheckJoin("10.0.0.5", port));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("27600")]
        [InlineData("65535")]
        public void CheckJoin_AcceptsTheEdgesOfTheUsableRange(string port)
        {
            Assert.Equal(ConnectProblem.None, ConnectRules.CheckJoin("10.0.0.5", port));
        }

        [Fact]
        public void CheckJoin_ForAPortTooLongToBeANumber_RefusesItRatherThanOverflowing()
        {
            // All digits, so the character check passes, but it does not fit in
            // an int. Without this the parse failure is silent and the value
            // falls through as whatever the parser left behind.
            Assert.Equal(ConnectProblem.PortOutOfRange, ConnectRules.CheckJoin("10.0.0.5", "99999999999"));

            Assert.False(ConnectRules.TryParsePort("99999999999", out var port));
            Assert.Equal(ConnectSettings.DefaultPort, port);
        }

        [Fact]
        public void TryParsePort_ReportsBothTheVerdictAndTheValue()
        {
            Assert.True(ConnectRules.TryParsePort("27601", out var good));
            Assert.Equal(27_601, good);

            Assert.False(ConnectRules.TryParsePort("nope", out var bad));
            Assert.Equal(ConnectSettings.DefaultPort, bad);
        }

        // --- which problem is reported first ---

        [Fact]
        public void CheckHost_ReportsTheAddressBeforeThePort()
        {
            // One problem is shown at a time, so the order is what the player is
            // told to fix first. Most fundamental first.
            Assert.Equal(ConnectProblem.BindNotAnIpAddress, ConnectRules.CheckHost("nope", "wat", null));
        }

        [Fact]
        public void CheckHost_ReportsThePortBeforeTheMissingPassphrase()
        {
            Assert.Equal(ConnectProblem.PortUnreadable, ConnectRules.CheckHost("0.0.0.0", "wat", null));
        }

        // --- the form itself ---

        [Fact]
        public void NewForm_StartsOnTheDefaultPortAndNothingElse()
        {
            var form = new ConnectForm();
            Assert.Equal("27600", form.PortText);
            Assert.Equal(string.Empty, form.AddressText);
            Assert.False(form.RememberPassphrase);
        }

        [Fact]
        public void CanJoinAndCanHost_TrackTheirOwnRulesIndependently()
        {
            // A DNS name is joinable and not hostable, from the same field.
            var form = new ConnectForm { AddressText = "friend.example.com" };

            Assert.True(form.CanJoin);
            Assert.False(form.CanHost);
            Assert.Equal(ConnectProblem.BindNotAnIpAddress, form.HostProblem);
        }

        [Fact]
        public void Port_ExposesTheParsedValueForTheCaller()
        {
            var form = new ConnectForm { AddressText = "10.0.0.5", PortText = "27601" };
            Assert.Equal(27_601, form.Port);
        }

        [Fact]
        public void Port_WhenUnreadable_FallsBackToTheDefaultRatherThanZero()
        {
            var form = new ConnectForm { AddressText = "10.0.0.5", PortText = "wat" };
            Assert.Equal(ConnectSettings.DefaultPort, form.Port);
            Assert.False(form.CanJoin);
        }

        [Fact]
        public void FromSettings_ThenToSettings_RoundTripsWithoutLosingTheRememberFlag()
        {
            var stored = new ConnectSettings("10.0.0.5", 27_601, true, "hunter2");
            var form = ConnectForm.FromSettings(stored);

            Assert.Equal("10.0.0.5", form.AddressText);
            Assert.Equal("27601", form.PortText);
            Assert.Equal("hunter2", form.Passphrase);
            Assert.True(form.RememberPassphrase);

            var round = form.ToSettings();
            Assert.Equal(stored.Address, round.Address);
            Assert.Equal(stored.Port, round.Port);
            Assert.Equal(stored.Passphrase, round.Passphrase);
            Assert.True(round.RememberPassphrase);
        }

        [Fact]
        public void FromSettings_OnNull_GivesAUsableEmptyForm()
        {
            var form = ConnectForm.FromSettings(null);
            Assert.Equal("27600", form.PortText);
            Assert.Equal(string.Empty, form.AddressText);
        }

        [Fact]
        public void ToSettings_WhenNotRemembering_CarriesNoPassphrase()
        {
            // Belt and braces with ConnectSettings.Serialize: the passphrase
            // should not even reach the object that knows how to write it.
            var form = new ConnectForm
            {
                AddressText = "10.0.0.5",
                Passphrase = "hunter2",
                RememberPassphrase = false,
            };

            Assert.Equal(string.Empty, form.ToSettings().Passphrase);
        }

        [Fact]
        public void SettingANullOnAFieldIsTreatedAsEmpty()
        {
            // The fields are driven straight from UIInput.value, which can be
            // null before the widget has started.
            var form = new ConnectForm { AddressText = null!, PortText = null!, Passphrase = null! };

            Assert.Equal(string.Empty, form.AddressText);
            Assert.Equal(string.Empty, form.Passphrase);
            Assert.Equal(ConnectProblem.AddressEmpty, form.JoinProblem);
        }
    }
}
