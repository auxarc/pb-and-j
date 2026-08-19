using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Where to start reading. The fixture most of the parts build their snapshots
    // with, and the tests over DestructionUpdate itself -- the type Receive and
    // SettleWindow return, which no one subject file owns.
    //
    // DestructionStateTests is one class split across this file and its siblings,
    // each named for its subject. DestructionRampTests, a separate class that shared
    // the original file, is in .Ramp.cs.
    public partial class DestructionStateTests
    {
        private static UnitSnapshot Unit(string? name, params PartDestruction[] parts)
        {
            return new UnitSnapshot(
                name,
                default,
                default,
                default,
                1f,
                wreckedParts: parts);
        }

        private static PartDestruction Part(string? socket, float time)
        {
            return new PartDestruction(socket, time);
        }

        private static UnitSnapshot Wreck(string? name, float at, params PartDestruction[] parts)
        {
            return new UnitSnapshot(
                name, default, default, default, 1f,
                isWrecked: true, wreckedAt: at, wreckedParts: parts);
        }

        [Fact]
        public void Update_Nothing_IsEmptyOnBothLists()
        {
            Assert.True(DestructionUpdate.Nothing.IsEmpty);
            Assert.Empty(DestructionUpdate.Nothing.Parts);
            Assert.Empty(DestructionUpdate.Nothing.Units);
        }

        [Fact]
        public void Update_WithEitherListPopulated_IsNotEmpty()
        {
            Assert.False(new DestructionUpdate(
                new[] { new DestructionDrive("a", "core", true) }, null).IsEmpty);
            Assert.False(new DestructionUpdate(
                null, new[] { new UnitWreckDrive("a", true) }).IsEmpty);
        }
    }
}
