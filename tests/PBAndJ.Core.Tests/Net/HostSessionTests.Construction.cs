using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Constructing a HostSession: what the arguments must be, and what is refused.
    // One section of the original, moved whole.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- construction ---

        [Fact]
        public void Constructor_StartsInPlanningWhenAlreadyInCombat()
        {
            Assert.Equal(HostSessionState.Planning, Host().State);
        }

        [Fact]
        public void Constructor_StartsInLobbyWhenNotInCombat()
        {
            bridge.InCombat = false;
            Assert.Equal(HostSessionState.Lobby, Host().State);
        }

        [Fact]
        public void Constructor_CountsTheHostAsAParticipant()
        {
            Assert.Equal(1, Host().ParticipantCount);
        }

        [Fact]
        public void Constructor_TakesTheCurrentTurnFromTheBridge()
        {
            bridge.CurrentTurn = 9;
            Assert.Equal(9, Host().Turn);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankHostName_Throws(string? name)
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession(name!, "s", 3, bridge, "secret", SessionRequirements.None));
            Assert.Equal("hostName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithBlankSessionId_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession("h", " ", 3, bridge, "secret", SessionRequirements.None));
            Assert.Equal("sessionId", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new HostSession("h", "s", 3, null!, "secret", SessionRequirements.None));
            Assert.Equal("bridge", ex.ParamName);
        }

        // No permissive default: "accept anything" has to be spelled
        // SessionRequirements.None at the call site, so opening a session to
        // anyone is always something someone typed.
        [Fact]
        public void Constructor_WithNullRequirements_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new HostSession("h", "s", 3, bridge, "secret", null!));
            Assert.Equal("requirements", ex.ParamName);
        }
    }
}
