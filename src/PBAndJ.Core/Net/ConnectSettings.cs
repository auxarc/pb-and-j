using System;
using System.Globalization;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// The address, port and (optionally) passphrase the connect screen
    /// remembers between sessions.
    /// </summary>
    /// <remarks>
    /// The format is a small <c>key: value</c> line subset — readable by eye,
    /// editable by hand, and parseable here without Core gaining a YAML
    /// dependency it is not allowed to have. It also happens to be valid YAML,
    /// so the game's own reader could load it if this ever needs to move.
    /// <para>
    /// The file lives beside the game's own settings rather than inside the mod
    /// folder, because <c>make deploy</c> deletes and recreates that folder on
    /// every redeploy — which is exactly the machine where this gets exercised
    /// most.
    /// </para>
    /// <para>
    /// The passphrase is written only when <see cref="RememberPassphrase"/> says
    /// so. It is stored in the clear, which the screen has to say out loud: the
    /// protocol already sends it in the clear over plain TCP, so a file is not
    /// the weak link, but somebody agreeing to "remember this" has not thereby
    /// agreed to "write my shared secret to disk" unless they were told.
    /// </para>
    /// </remarks>
    public sealed class ConnectSettings
    {
        /// <summary>The port the rest of the mod defaults to.</summary>
        public const int DefaultPort = 27600;

        private const string AddressKey = "address";
        private const string PortKey = "port";
        private const string RememberKey = "remember-passphrase";
        private const string PassphraseKey = "passphrase";

        public ConnectSettings(string? address, int port, bool rememberPassphrase, string? passphrase)
        {
            Address = address ?? string.Empty;
            Port = port;
            RememberPassphrase = rememberPassphrase;
            Passphrase = passphrase ?? string.Empty;
        }

        /// <summary>Nothing remembered yet: no address, the default port, no passphrase.</summary>
        public static ConnectSettings Default { get; } =
            new ConnectSettings(string.Empty, DefaultPort, false, string.Empty);

        public string Address { get; }
        public int Port { get; }
        public bool RememberPassphrase { get; }
        public string Passphrase { get; }

        public string Serialize()
        {
            var text = new StringBuilder();
            text.Append("# pb-and-j connect settings — edit by hand if you like.\n");

            // A remembered passphrase is stored in the clear. Saying so in the
            // file itself costs nothing and means the fact is discoverable by
            // whoever opens it, not only by whoever read the tickbox.
            if (RememberPassphrase)
            {
                text.Append("# The passphrase below is NOT encrypted.\n");
            }

            text.Append(AddressKey).Append(": ").Append(Address).Append('\n');
            text.Append(PortKey).Append(": ")
                .Append(Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append(RememberKey).Append(": ")
                .Append(RememberPassphrase ? "true" : "false").Append('\n');

            // A newline in the value would forge a second setting on the line
            // after it. The UI field is single-line so this cannot arrive from
            // the screen, but the file is hand-editable and round-trips through
            // here, so the writer is the guard.
            if (RememberPassphrase && Passphrase.Length > 0 && !HasLineBreak(Passphrase))
            {
                text.Append(PassphraseKey).Append(": ").Append(Passphrase).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads a settings file. Returns false when there was nothing usable to
        /// read, but <paramref name="settings"/> is always populated.
        /// </summary>
        /// <remarks>
        /// Never throws. This runs while the title screen is being built, and a
        /// preference file nobody can parse must not be able to take the main
        /// menu down with it — every unreadable field falls back rather than
        /// failing.
        /// </remarks>
        public static bool TryParse(string? stored, out ConnectSettings settings)
        {
            settings = Default;

            if (string.IsNullOrWhiteSpace(stored))
            {
                return false;
            }

            var address = Default.Address;
            var port = Default.Port;
            var remember = false;
            var passphrase = string.Empty;
            var understood = false;

            foreach (var rawLine in stored!.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var split = line.IndexOf(':');
                if (split <= 0)
                {
                    continue;
                }

                // Split on the FIRST colon only, so a passphrase may contain
                // as many more as it likes.
                var key = line.Substring(0, split).Trim();
                var value = line.Substring(split + 1).Trim();

                switch (key)
                {
                    case AddressKey:
                        address = value;
                        understood = true;
                        break;

                    case PortKey:
                        // An unreadable or out-of-range port falls back rather
                        // than failing. Port 0 in particular would "work" — the
                        // OS hands out an arbitrary free port — and then be
                        // unreachable by the person we just told to connect.
                        port = TryReadPort(value, out var parsed) ? parsed : Default.Port;
                        understood = true;
                        break;

                    case RememberKey:
                        remember = IsYes(value);
                        understood = true;
                        break;

                    case PassphraseKey:
                        passphrase = value;
                        understood = true;
                        break;

                    // Anything else is from a newer build. Ignored, so a file
                    // written by one does not reset the settings of another.
                }
            }

            // The flag is the consent record, so it wins over a stray passphrase
            // line that a hand edit could have left behind.
            if (!remember)
            {
                passphrase = string.Empty;
            }

            settings = new ConnectSettings(address, port, remember, passphrase);
            return understood;
        }

        private static bool TryReadPort(string value, out int port)
        {
            port = Default.Port;

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (parsed < 1 || parsed > 65535)
            {
                return false;
            }

            port = parsed;
            return true;
        }

        /// <summary>
        /// Anything not recognisably yes is no — consent is opt-in, so an
        /// unreadable flag must not be read as agreement.
        /// </summary>
        private static bool IsYes(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasLineBreak(string value)
        {
            return value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
        }
    }
}
