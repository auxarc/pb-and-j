using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>
    /// What the connect screen remembers between sessions, and how it survives a
    /// file somebody has edited by hand.
    /// </summary>
    public class ConnectSettingsTests
    {
        // --- the one that matters ---

        [Fact]
        public void Serialize_WhenNotRemembered_DoesNotLeakThePassphraseToDisk()
        {
            // The tickbox is the whole consent model. A passphrase written to
            // disk by a screen that promised not to is the worst bug this file
            // can have, and the only one nobody would notice.
            var settings = new ConnectSettings("10.0.0.5", 27600, rememberPassphrase: false, passphrase: "hunter2");

            Assert.DoesNotContain("hunter2", settings.Serialize());
        }

        [Fact]
        public void RoundTrip_WhenRemembered_KeepsThePassphrase()
        {
            var settings = new ConnectSettings("10.0.0.5", 27600, rememberPassphrase: true, passphrase: "hunter2");

            Assert.True(ConnectSettings.TryParse(settings.Serialize(), out var read));
            Assert.Equal("hunter2", read.Passphrase);
            Assert.True(read.RememberPassphrase);
        }

        [Fact]
        public void RoundTrip_PreservesAPassphraseContainingTheSeparator()
        {
            // "key: value" splits on the first colon only, so a passphrase may
            // contain as many as it likes.
            var settings = new ConnectSettings("host.example.com", 27600, true, "a:b:c");

            Assert.True(ConnectSettings.TryParse(settings.Serialize(), out var read));
            Assert.Equal("a:b:c", read.Passphrase);
        }

        [Fact]
        public void RoundTrip_PreservesAddressAndPort()
        {
            var settings = new ConnectSettings("host.example.com", 27_601, false, null);

            Assert.True(ConnectSettings.TryParse(settings.Serialize(), out var read));
            Assert.Equal("host.example.com", read.Address);
            Assert.Equal(27_601, read.Port);
        }

        [Fact]
        public void Serialize_RefusesAPassphraseCarryingANewline()
        {
            // A newline would forge a second setting on the next line. The field
            // is single-line so this cannot arrive from the UI, but the file is
            // hand-editable and the writer is the only guard.
            var settings = new ConnectSettings("10.0.0.5", 27600, true, "one\ntwo: three");
            var text = settings.Serialize();

            Assert.DoesNotContain("one", text);
            Assert.True(ConnectSettings.TryParse(text, out var read));
            Assert.Equal(string.Empty, read.Passphrase);
        }

        // --- surviving a file somebody edited ---

        [Fact]
        public void TryParse_OnGarbage_ReturnsDefaultsRatherThanThrowingIntoTheMainMenu()
        {
            // This is read while the title screen is building. An exception here
            // would take the menu down over a corrupt preference.
            Assert.False(ConnectSettings.TryParse("\0\0 not settings �", out var read));
            Assert.Equal(ConnectSettings.Default.Address, read.Address);
            Assert.Equal(ConnectSettings.Default.Port, read.Port);
        }

        [Fact]
        public void TryParse_OnNullOrEmpty_ReturnsDefaults()
        {
            Assert.False(ConnectSettings.TryParse(null, out var fromNull));
            Assert.Equal(ConnectSettings.Default.Port, fromNull.Port);

            Assert.False(ConnectSettings.TryParse("   ", out var fromBlank));
            Assert.Equal(ConnectSettings.Default.Port, fromBlank.Port);
        }

        [Fact]
        public void TryParse_OnAnUnreadablePort_FallsBackToTheDefaultRatherThanZero()
        {
            // Port 0 asks the OS for an arbitrary free port, which would "work"
            // and then be unreachable by the person we just told to connect.
            Assert.True(ConnectSettings.TryParse("address: 10.0.0.5\nport: wat\n", out var read));
            Assert.Equal(ConnectSettings.Default.Port, read.Port);
            Assert.Equal("10.0.0.5", read.Address);
        }

        [Fact]
        public void TryParse_OnAnOutOfRangePort_FallsBackToTheDefault()
        {
            Assert.True(ConnectSettings.TryParse("port: 70000\n", out var high));
            Assert.Equal(ConnectSettings.Default.Port, high.Port);

            Assert.True(ConnectSettings.TryParse("port: 0\n", out var zero));
            Assert.Equal(ConnectSettings.Default.Port, zero.Port);
        }

        [Fact]
        public void TryParse_IgnoresCommentsAndBlankLines()
        {
            const string text = "# written by pb-and-j\n\naddress: 10.0.0.5\n\n  # trailing note\n";

            Assert.True(ConnectSettings.TryParse(text, out var read));
            Assert.Equal("10.0.0.5", read.Address);
        }

        [Fact]
        public void TryParse_IgnoresAKeyItDoesNotKnow()
        {
            // Forward compatibility: a newer build's file must not reset an
            // older one's settings just by mentioning something new.
            Assert.True(ConnectSettings.TryParse("address: 10.0.0.5\nfuture-thing: 4\n", out var read));
            Assert.Equal("10.0.0.5", read.Address);
        }

        [Fact]
        public void TryParse_WhenRememberIsOffButAPassphraseIsPresent_IgnoresIt()
        {
            // Somebody hand-editing the file could leave the two disagreeing.
            // The flag is the consent record, so it wins.
            const string text = "remember-passphrase: false\npassphrase: hunter2\n";

            Assert.True(ConnectSettings.TryParse(text, out var read));
            Assert.Equal(string.Empty, read.Passphrase);
            Assert.False(read.RememberPassphrase);
        }

        [Theory]
        [InlineData("true")]
        [InlineData("True")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        public void TryParse_AcceptsTheObviousSpellingsOfTrue(string written)
        {
            Assert.True(ConnectSettings.TryParse(
                "remember-passphrase: " + written + "\npassphrase: hunter2\n", out var read));
            Assert.True(read.RememberPassphrase);
            Assert.Equal("hunter2", read.Passphrase);
        }

        [Fact]
        public void TryParse_TreatsAnUnreadableFlagAsNotRemembering()
        {
            // Consent is opt-in, so anything that is not recognisably yes is no.
            Assert.True(ConnectSettings.TryParse("remember-passphrase: maybe\npassphrase: x\n", out var read));
            Assert.False(read.RememberPassphrase);
            Assert.Equal(string.Empty, read.Passphrase);
        }

        [Fact]
        public void Default_UsesThePortTheRestOfTheModDefaultsTo()
        {
            Assert.Equal(27600, ConnectSettings.Default.Port);
            Assert.Equal(string.Empty, ConnectSettings.Default.Address);
            Assert.False(ConnectSettings.Default.RememberPassphrase);
            Assert.Equal(string.Empty, ConnectSettings.Default.Passphrase);
        }

        [Fact]
        public void Constructor_NormalisesNullsToEmpty()
        {
            var settings = new ConnectSettings(null, 27600, false, null);
            Assert.Equal(string.Empty, settings.Address);
            Assert.Equal(string.Empty, settings.Passphrase);
        }

        [Fact]
        public void Serialize_IsReadableAndSaysWhatWroteIt()
        {
            // The file sits beside the game's own settings and is meant to be
            // hand-editable; an unlabelled blob there is hostile.
            var text = new ConnectSettings("10.0.0.5", 27600, false, null).Serialize();
            Assert.StartsWith("#", text);
            Assert.Contains("pb-and-j", text);
        }
    }
}
