using System;
using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class OrderPayloadTests
    {
        private static OrderPayload Order(
            string blueprint = "move_run",
            string ownerName = "unit_a",
            float start = 0f,
            float dur = 2.5f,
            IReadOnlyList<Vec3>? points = null,
            IReadOnlyList<PathLink>? links = null) =>
            new OrderPayload(blueprint, ownerName, start, dur, pathPoints: points, pathLinks: links);

        private static readonly Vec3[] TwoPoints = { new Vec3(0f, 0f, 0f), new Vec3(10f, 0f, 0f) };
        private static readonly PathLink[] OneLink = { new PathLink(0, 0) };

        [Fact]
        public void Constructor_RetainsAllFields()
        {
            var order = new OrderPayload(
                "melee_charge", "unit_z", 1.25f, 3.5f,
                targetedPoint: new Vec3(1f, 2f, 3f),
                targetedDirection: new Vec3(0f, 1f, 0f),
                targetedEntityName: "unit_enemy",
                targetedSocketName: "torso",
                targetedHardpointName: "hp_main",
                pathPoints: TwoPoints,
                pathLinks: OneLink,
                directionInterpolant: 0.5f,
                offsetInterpolant: 0.25f,
                melee: new MeleePayload(true, "shock_a", true),
                dash: new DashPayload(0.4f, 0.6f),
                dashVertical: new DashVerticalPayload(new Vec3(4f, 5f, 6f), 12f),
                dashBulldoze: new[] { new DestructionPoint(0.5f, new Vec3(7f, 8f, 9f), 3) });

            Assert.Equal("melee_charge", order.Blueprint);
            Assert.Equal("unit_z", order.OwnerName);
            Assert.Equal(1.25f, order.StartTime);
            Assert.Equal(3.5f, order.Duration);
            Assert.Equal(new Vec3(1f, 2f, 3f), order.TargetedPoint);
            Assert.Equal(new Vec3(0f, 1f, 0f), order.TargetedDirection);
            Assert.Equal("unit_enemy", order.TargetedEntityName);
            Assert.Equal("torso", order.TargetedSocketName);
            Assert.Equal("hp_main", order.TargetedHardpointName);
            Assert.Equal(2, order.PathPoints.Count);
            Assert.Single(order.PathLinks);
            Assert.Equal(0.5f, order.DirectionInterpolant);
            Assert.Equal(0.25f, order.OffsetInterpolant);
            Assert.True(order.Melee!.IsOffsetRight);
            Assert.Equal("shock_a", order.Melee.ShockwaveKey);
            Assert.True(order.Melee.PartUsed);
            Assert.Equal(0.4f, order.Dash!.DurationDashOut);
            Assert.Equal(0.6f, order.Dash.DurationDashAlign);
            Assert.Equal(new Vec3(4f, 5f, 6f), order.DashVertical!.Origin);
            Assert.Equal(12f, order.DashVertical.Altitude);
            Assert.Single(order.DashBulldoze);
            Assert.Equal(3, order.DashBulldoze[0].Index);
        }

        [Fact]
        public void Constructor_WithNoOptionalFields_LeavesThemNullOrEmpty()
        {
            var order = Order();
            Assert.Null(order.TargetedPoint);
            Assert.Null(order.TargetedDirection);
            Assert.Null(order.TargetedEntityName);
            Assert.Null(order.TargetedSocketName);
            Assert.Null(order.TargetedHardpointName);
            Assert.Empty(order.PathPoints);
            Assert.Empty(order.PathLinks);
            Assert.Null(order.DirectionInterpolant);
            Assert.Null(order.OffsetInterpolant);
            Assert.Null(order.Melee);
            Assert.Null(order.Dash);
            Assert.Null(order.DashVertical);
            Assert.Empty(order.DashBulldoze);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_WithMissingBlueprint_Throws(string? blueprint)
        {
            var ex = Assert.Throws<ArgumentException>(() => Order(blueprint: blueprint!));
            Assert.Equal("blueprint", ex.ParamName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_WithMissingOwnerName_Throws(string? ownerName)
        {
            var ex = Assert.Throws<ArgumentException>(() => Order(ownerName: ownerName!));
            Assert.Equal("ownerName", ex.ParamName);
        }

        // The game's own gate (ActionUtility.CreatePathAction) requires at least
        // two points and at least one link, but does NOT require links to number
        // points-1. Mirror exactly that — over-constraining here would reject
        // orders the game itself accepts.

        [Fact]
        public void Constructor_WithMovementPath_AcceptsTwoPointsAndOneLink()
        {
            var order = Order(points: TwoPoints, links: OneLink);
            Assert.Equal(2, order.PathPoints.Count);
            Assert.Single(order.PathLinks);
        }

        [Fact]
        public void Constructor_WithMoreLinksThanPointsMinusOne_IsAccepted()
        {
            var order = Order(points: TwoPoints, links: new[] { new PathLink(0, 0), new PathLink(1, 1) });
            Assert.Equal(2, order.PathLinks.Count);
        }

        [Fact]
        public void Constructor_WithSinglePathPoint_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => Order(points: new[] { new Vec3(0f, 0f, 0f) }, links: OneLink));
            Assert.Equal("pathPoints", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithPointsButNoLinks_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Order(points: TwoPoints, links: new PathLink[0]));
            Assert.Equal("pathLinks", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithLinksButNoPoints_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Order(points: new Vec3[0], links: OneLink));
            Assert.Equal("pathPoints", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithTooManyPathPoints_Throws()
        {
            var points = new Vec3[OrderPayload.MaxPathPoints + 1];
            var ex = Assert.Throws<ArgumentException>(() => Order(points: points, links: OneLink));
            Assert.Equal("pathPoints", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithTooManyPathLinks_Throws()
        {
            var links = new PathLink[OrderPayload.MaxPathPoints + 1];
            var ex = Assert.Throws<ArgumentException>(() => Order(points: TwoPoints, links: links));
            Assert.Equal("pathLinks", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithTooManyDestructionPoints_Throws()
        {
            var bulldoze = new DestructionPoint[OrderPayload.MaxPathPoints + 1];
            var ex = Assert.Throws<ArgumentException>(
                () => new OrderPayload("dash", "unit_a", 0f, 1f, dashBulldoze: bulldoze));
            Assert.Equal("dashBulldoze", ex.ParamName);
        }

        // --- value types ---

        [Fact]
        public void Vec3_RetainsComponents()
        {
            var v = new Vec3(1.5f, -2.5f, 3.5f);
            Assert.Equal(1.5f, v.X);
            Assert.Equal(-2.5f, v.Y);
            Assert.Equal(3.5f, v.Z);
        }

        [Fact]
        public void PathLink_RetainsComponents()
        {
            var link = new PathLink(2, 7);
            Assert.Equal(2, link.Type);
            Assert.Equal(7, link.DestinationIndex);
        }

        [Fact]
        public void DestructionPoint_RetainsComponents()
        {
            var point = new DestructionPoint(1.25f, new Vec3(1f, 2f, 3f), 9);
            Assert.Equal(1.25f, point.Time);
            Assert.Equal(new Vec3(1f, 2f, 3f), point.Position);
            Assert.Equal(9, point.Index);
        }

        [Fact]
        public void MeleePayload_RetainsFields()
        {
            var melee = new MeleePayload(false, null, true);
            Assert.False(melee.IsOffsetRight);
            Assert.Null(melee.ShockwaveKey);
            Assert.True(melee.PartUsed);
        }

        [Fact]
        public void DashPayload_RetainsFields()
        {
            var dash = new DashPayload(1.5f, 2.5f);
            Assert.Equal(1.5f, dash.DurationDashOut);
            Assert.Equal(2.5f, dash.DurationDashAlign);
        }

        [Fact]
        public void DashVerticalPayload_RetainsFields()
        {
            var vertical = new DashVerticalPayload(new Vec3(1f, 2f, 3f), 4f);
            Assert.Equal(new Vec3(1f, 2f, 3f), vertical.Origin);
            Assert.Equal(4f, vertical.Altitude);
        }
    }
}
