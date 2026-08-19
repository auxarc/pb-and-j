using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // What the runtime refuses to be built without, and the one thing it exposes
    // about the session it was built with.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
        // --- construction ---

        [Fact]
        public void Constructor_WithNullTransport_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(null!, bridge, log, mailbox, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None)));
            Assert.Equal("transport", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, null!, log, mailbox, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None)));
            Assert.Equal("bridge", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullLog_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, null!, mailbox, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None)));
            Assert.Equal("log", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullMailbox_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, log, null!, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None)));
            Assert.Equal("mailbox", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullSession_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, log, mailbox, null!));
            Assert.Equal("session", ex.ParamName);
        }

        [Fact]
        public void Session_ExposesTheSession()
        {
            var runtime = HostRuntime();
            Assert.Same(host, runtime.Session);
        }
    }
}
