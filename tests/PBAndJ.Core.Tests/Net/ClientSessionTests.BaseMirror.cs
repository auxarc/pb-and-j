using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The base mirror (M12a): what a client does with the host's base position.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- the base mirror (M12a) ---

        [Fact]
        public void BasePosition_BecomesAMirrorEffect()
        {
            var client = Welcomed();

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1024.5f, -37.25f));

            var mirror = Single<MirrorBaseEffect>(effects);
            Assert.Equal(1024.5f, mirror.X);
            Assert.Equal(-37.25f, mirror.Z);
        }
    }
}
