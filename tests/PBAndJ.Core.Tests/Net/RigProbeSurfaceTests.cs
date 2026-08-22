using System.Reflection;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The build-time half of a rig instrument's vacuity guard.
    //
    // CombatEdgeProbeGlue (src/PBAndJ.Mod/Net/CombatEdgeProbeGlue.cs) answers
    // R0's re-entry-edge question by reading four PRIVATE members of PbjRuntime
    // by name, through reflection, because they are the values the edge is
    // actually computed from and a re-derived copy of the rule would stop being
    // the session's answer the moment the rule changed.
    //
    // 🔴 A private member is not API and nothing stops it being renamed. When it
    // is, the probe's Resolve() refuses to arm and says which name it lost —
    // which is the right behaviour at 1am on the rig and the wrong place to find
    // out. These tests move that discovery to the build, on the machine of
    // whoever does the rename, which matters right now because M12c's lane is
    // editing this very file.
    //
    // The Mod assembly is not referenced from here (it targets net472 against
    // the game's vendored DLLs), so this covers the four Core-side names and NOT
    // NetGlue.runtime, the fifth. That gap is real; it is stated in
    // docs/notes/rig-run-1-0.md rather than papered over.
    public class RigProbeSurfaceTests
    {
        [Theory]
        [InlineData("lastInCombat")]
        [InlineData("lastTickSeconds")]
        [InlineData("bridge")]
        [InlineData("stopped")]
        public void PbjRuntimeStillHoldsTheFieldTheEdgeProbeReads(string name)
        {
            var field = typeof(PbjRuntime).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.True(
                field != null,
                "PbjRuntime." + name + " is gone. CombatEdgeProbeGlue reads it by reflection to "
                + "count the combat edge; without it the probe refuses to arm and R0's re-entry-edge "
                + "reading cannot be taken. Rename it there too, or give the probe another way in.");
        }

        // The names above are only worth asserting if a wrong one would fail, and
        // this repo has already banked a control case that could not reach the
        // shape it guarded. This one runs the identical lookup against a name
        // deliberately not present, so the assertion above is known to be capable
        // of failing rather than assumed to be.
        [Fact]
        public void TheSameLookupFindsNothingForANameThatIsNotThere()
        {
            var field = typeof(PbjRuntime).GetField(
                "lastInCombatButRenamed", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.Null(field);
        }

        // And the types, because a field that survives a rename into a different
        // type is the legal-but-wrong version the neighbouring check is blind to:
        // the probe casts through `is bool` and `is double`, and a silent type
        // change would make every sample fall through to no reading at all — a
        // run of zeros that looks exactly like "the edge never fired".
        [Theory]
        [InlineData("lastInCombat", typeof(bool))]
        [InlineData("lastTickSeconds", typeof(double))]
        [InlineData("stopped", typeof(bool))]
        [InlineData("bridge", typeof(IPbjGameBridge))]
        public void TheFieldTheEdgeProbeReadsIsStillTheTypeItCastsTo(string name, System.Type expected)
        {
            var field = typeof(PbjRuntime).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);
            Assert.Equal(expected, field!.FieldType);
        }
    }
}
