using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PassengerRulesTests
    {
        // Every control the 2026-08-08 review found a client can reach. The
        // first draft of M12a listed only the first of these and called the
        // suppression complete.
        public static TheoryData<OverworldControl> EveryControl => new TheoryData<OverworldControl>
        {
            OverworldControl.MoveBase,
            OverworldControl.Camp,
            OverworldControl.Retreat,
            OverworldControl.EngageSite,
        };

        // --- who may drive ---

        [Theory]
        [MemberData(nameof(EveryControl))]
        public void Solo_MayDoEverything(OverworldControl control)
        {
            // Nothing about this milestone should change a singleplayer game.
            Assert.True(PassengerRules.Allows(SessionRole.Solo, control));
        }

        [Theory]
        [MemberData(nameof(EveryControl))]
        public void TheHost_MayDoEverything(OverworldControl control)
        {
            Assert.True(PassengerRules.Allows(SessionRole.Host, control));
        }

        [Theory]
        [MemberData(nameof(EveryControl))]
        public void AClient_MayDoNoneOfIt(OverworldControl control)
        {
            Assert.False(PassengerRules.Allows(SessionRole.Client, control));
        }

        [Fact]
        public void AnUnknownRole_IsTreatedAsAPassenger()
        {
            // Fail closed. A role we do not recognise is not evidence of
            // authority, and letting it drive would desynchronise the base
            // silently — the failure mode this milestone exists to design out.
            Assert.False(PassengerRules.Allows((SessionRole)99, OverworldControl.MoveBase));
        }

        // --- camp and retreat are here for a measured reason ---

        [Fact]
        public void Camp_IsRefusedEvenThoughItIssuesNoMovementOrder()
        {
            // OverworldUtility.OrderMovementToPosition is not the funnel the
            // first draft claimed. CIViewOverworldRoster.OnCampInitiated starts
            // a SimulationLockCountdown, and OverworldTimeUtility.RefreshTimeScale
            // skips its isBaseMoving derivation entirely while a lock exists, so
            // a client can run the shared clock without ever ordering a move.
            Assert.False(PassengerRules.Allows(SessionRole.Client, OverworldControl.Camp));
        }

        [Fact]
        public void Retreat_IsRefusedBecauseItRelocatesTheBaseOutsideTheFunnel()
        {
            // TryRetreatToResupplyBase starts a "resupply" lock AND a
            // SimulationLockReposition, which moves the base without touching
            // OrderMovementToPosition at all.
            Assert.False(PassengerRules.Allows(SessionRole.Client, OverworldControl.Retreat));
        }

        // --- the refusals a player actually sees ---

        [Theory]
        [MemberData(nameof(EveryControl))]
        public void EveryRefusal_SaysSomething(OverworldControl control)
        {
            var refusal = PassengerText.Refusal(control);

            Assert.False(string.IsNullOrWhiteSpace(refusal));
        }

        [Theory]
        [MemberData(nameof(EveryControl))]
        public void EveryRefusal_NamesTheHostRatherThanBlamingThePlayer(OverworldControl control)
        {
            // A refusal has to explain whose authority it is, or it reads as a
            // broken button. This is the connect screen's lesson generalised: a
            // silent no and a bug are indistinguishable to the person clicking.
            Assert.Contains("host", PassengerText.Refusal(control), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheRefusals_AreAllDifferent()
        {
            // Four buttons that all say the same sentence teach the player
            // nothing about which authority they just met.
            var refusals = new[]
            {
                PassengerText.Refusal(OverworldControl.MoveBase),
                PassengerText.Refusal(OverworldControl.Camp),
                PassengerText.Refusal(OverworldControl.Retreat),
                PassengerText.Refusal(OverworldControl.EngageSite),
            };

            Assert.Equal(refusals.Length, new System.Collections.Generic.HashSet<string>(refusals).Count);
        }

        [Fact]
        public void CampsRefusal_MentionsTheClockRatherThanMovement()
        {
            // The player's mental model of camping is rest, not driving. Telling
            // them "the host drives the base" when they clicked Camp would be
            // true of the mechanism and useless as an explanation.
            Assert.Contains("clock", PassengerText.Refusal(OverworldControl.Camp), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnUnknownControl_StillExplainsItself()
        {
            // A new control added to the enum without a sentence here must not
            // produce an empty tooltip on a live screen.
            Assert.False(string.IsNullOrWhiteSpace(PassengerText.Refusal((OverworldControl)99)));
        }
    }
}
