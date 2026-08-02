using System;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Thrown when bytes on the wire cannot be interpreted: a malformed frame,
    /// a truncated payload, an out-of-range length, or an unknown message type.
    /// The runtime treats this as grounds to disconnect the offending peer.
    /// </summary>
    /// <remarks>
    /// Exactly one constructor, deliberately. Unused overloads would be
    /// uncovered methods under the 100% gate.
    /// </remarks>
    public sealed class PbjProtocolException : Exception
    {
        public PbjProtocolException(string message)
            : base(message)
        {
        }
    }
}
