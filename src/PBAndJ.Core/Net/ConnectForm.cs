using System.Globalization;
using System.Net;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// What stands between these details and a connection, if anything.
    /// </summary>
    /// <remarks>
    /// Mostly things somebody typed, but not only —
    /// <see cref="SessionAlreadyRunning"/> is about the state of the machine
    /// rather than the state of the fields. Keeping both in one enum keeps
    /// <see cref="ConnectText.DescribeProblem"/> the single channel through
    /// which the screen explains itself.
    /// </remarks>
    public enum ConnectProblem : byte
    {
        None = 0,

        /// <summary>Nothing was entered to connect to.</summary>
        AddressEmpty = 1,

        /// <summary>The address is not a bare host — a pasted URL, or a port smuggled in.</summary>
        AddressMalformed = 2,

        /// <summary>A bind names an interface on this machine, so it must be an address.</summary>
        BindNotAnIpAddress = 3,

        /// <summary>The port is not plain digits.</summary>
        PortUnreadable = 4,

        /// <summary>The port is outside 1–65535.</summary>
        PortOutOfRange = 5,

        /// <summary>Listening on a routable address without a passphrase.</summary>
        OpenBindNeedsPassphrase = 6,

        /// <summary>
        /// A session already holds the port, so there is nothing to start.
        /// </summary>
        SessionAlreadyRunning = 7,
    }

    /// <summary>
    /// The rules behind the connect screen and <c>pbj.host</c>.
    /// </summary>
    /// <remarks>
    /// These live here rather than in the glue because they are decisions, and
    /// the glue is not measured. The bind and passphrase rules in particular
    /// used to sit inside <c>NetGlue.Host</c> — a real refusal, with real
    /// security reasoning behind it, outside the coverage gate. <c>NetGlue</c>
    /// now calls in here, so the screen and the console cannot disagree about
    /// what is allowed.
    /// <para>
    /// Joining and hosting are deliberately <em>not</em> the same rule.
    /// <c>pbj.join</c> has never parsed its address, because
    /// <c>TcpClient.Connect</c> resolves names and a friend has a hostname; a
    /// bind names an interface on this machine and has always required a literal
    /// address. Collapsing the two would break the first.
    /// </para>
    /// </remarks>
    public static class ConnectRules
    {
        public static bool TryParsePort(string? text, out int port)
        {
            port = ConnectSettings.DefaultPort;

            if (!IsPlainDigits(text))
            {
                return false;
            }

            // Digits already checked, so this can only fail by overflowing —
            // which the range check below would reject anyway.
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
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

        /// <summary>Whether these details could be used to join a host.</summary>
        public static ConnectProblem CheckJoin(string? address, string? portText)
        {
            var addressProblem = CheckJoinAddress(address);
            if (addressProblem != ConnectProblem.None)
            {
                return addressProblem;
            }

            return CheckPort(portText);
        }

        /// <summary>Whether these details could be used to host.</summary>
        public static ConnectProblem CheckHost(string? bind, string? portText, string? passphrase)
        {
            // Ordered most-fundamental first: only one problem is shown at a
            // time, so this is the order somebody is told to fix things in.
            if (!IPAddress.TryParse(Clean(bind), out var address))
            {
                return ConnectProblem.BindNotAnIpAddress;
            }

            var portProblem = CheckPort(portText);
            if (portProblem != ConnectProblem.None)
            {
                return portProblem;
            }

            return CheckPassphraseForBind(address, passphrase);
        }

        /// <summary>
        /// The host rules that do not involve the port field, for the console
        /// path, which already holds a parsed port.
        /// </summary>
        public static ConnectProblem CheckHostBind(string? bind, string? passphrase)
        {
            if (!IPAddress.TryParse(Clean(bind), out var address))
            {
                return ConnectProblem.BindNotAnIpAddress;
            }

            return CheckPassphraseForBind(address, passphrase);
        }

        /// <remarks>
        /// This protocol is open source and an accepted peer can submit orders
        /// for the units it is dealt, so a listener on a routable address with no
        /// passphrase is joinable by anything that finds the port. The passphrase
        /// travels in the clear over plain TCP: it keeps strangers out, it is not
        /// confidentiality against anyone on the path.
        /// </remarks>
        private static ConnectProblem CheckPassphraseForBind(IPAddress address, string? passphrase)
        {
            if (!IPAddress.IsLoopback(address) && string.IsNullOrWhiteSpace(passphrase))
            {
                return ConnectProblem.OpenBindNeedsPassphrase;
            }

            return ConnectProblem.None;
        }

        private static ConnectProblem CheckJoinAddress(string? address)
        {
            var cleaned = Clean(address);

            if (cleaned.Length == 0)
            {
                return ConnectProblem.AddressEmpty;
            }

            // Structural only. The job is to catch a pasted URL or a port typed
            // into the wrong box, not to out-guess the resolver about what is a
            // valid hostname.
            foreach (var c in cleaned)
            {
                if (c == '/' || c == '\\' || c == ':' || c == ' ' || c == '\t' || c == '@' || c == '?')
                {
                    return ConnectProblem.AddressMalformed;
                }
            }

            return ConnectProblem.None;
        }

        private static ConnectProblem CheckPort(string? portText)
        {
            if (!IsPlainDigits(portText))
            {
                return ConnectProblem.PortUnreadable;
            }

            return TryParsePort(portText, out _) ? ConnectProblem.None : ConnectProblem.PortOutOfRange;
        }

        /// <summary>
        /// Digits and nothing else — no sign, no spaces, no decimal point.
        /// </summary>
        /// <remarks>
        /// <c>int.TryParse</c> on its own accepts <c>" -1 "</c>, which is the
        /// same trap <c>ModVersion.TryPart</c> already documents.
        /// </remarks>
        private static bool IsPlainDigits(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var c in text!)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static string Clean(string? text)
        {
            return text == null ? string.Empty : text.Trim();
        }
    }

    /// <summary>
    /// The connect screen's state. Everything is a string because that is what a
    /// text field yields.
    /// </summary>
    public sealed class ConnectForm
    {
        private string address = string.Empty;
        private string portText = ConnectSettings.DefaultPort.ToString(CultureInfo.InvariantCulture);
        private string passphrase = string.Empty;

        /// <summary>The host to join, or the interface to bind when hosting.</summary>
        public string AddressText
        {
            // Driven straight from UIInput.value, which can be null before the
            // widget has started.
            get => address;
            set => address = value ?? string.Empty;
        }

        public string PortText
        {
            get => portText;
            set => portText = value ?? string.Empty;
        }

        public string Passphrase
        {
            get => passphrase;
            set => passphrase = value ?? string.Empty;
        }

        public bool RememberPassphrase { get; set; }

        /// <summary>Whether this machine is already in a session.</summary>
        /// <remarks>
        /// Session state rather than typed state, which is why it lives here and
        /// not in <see cref="ConnectRules"/> — those stay pure functions of what
        /// is in the fields, and <c>NetGlue</c> calls into them holding no form.
        /// The screen reads this from the live session every frame, so it has to
        /// clear as readily as it sets.
        /// </remarks>
        public bool SessionRunning { get; set; }

        public ConnectProblem JoinProblem => SessionRunning
            ? ConnectProblem.SessionAlreadyRunning
            : ConnectRules.CheckJoin(AddressText, PortText);

        // Reported ahead of anything wrong with the fields, on the same
        // most-fundamental-first ordering CheckHost follows: fixing a typo does
        // not help while a session holds the port.
        public ConnectProblem HostProblem => SessionRunning
            ? ConnectProblem.SessionAlreadyRunning
            : ConnectRules.CheckHost(AddressText, PortText, Passphrase);

        public bool CanJoin => JoinProblem == ConnectProblem.None;

        public bool CanHost => HostProblem == ConnectProblem.None;

        /// <summary>The parsed port, or the default when what was typed is unreadable.</summary>
        public int Port => ConnectRules.TryParsePort(PortText, out var parsed)
            ? parsed
            : ConnectSettings.DefaultPort;

        public static ConnectForm FromSettings(ConnectSettings? settings)
        {
            var source = settings ?? ConnectSettings.Default;

            return new ConnectForm
            {
                AddressText = source.Address,
                PortText = source.Port.ToString(CultureInfo.InvariantCulture),
                Passphrase = source.Passphrase,
                RememberPassphrase = source.RememberPassphrase,
            };
        }

        /// <remarks>
        /// A passphrase that is not being remembered is dropped here as well as
        /// in <see cref="ConnectSettings.Serialize"/> — it should not reach the
        /// object that knows how to write it, never mind the disk.
        /// </remarks>
        public ConnectSettings ToSettings()
        {
            return new ConnectSettings(
                AddressText,
                Port,
                RememberPassphrase,
                RememberPassphrase ? Passphrase : string.Empty);
        }
    }
}
