using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class OrderPayloadCodecTests
    {
        private static OrderPayload RoundTrip(OrderPayload order)
        {
            var writer = new PbjWriter();
            OrderPayloadCodec.Write(writer, order);
            var reader = new PbjReader(writer.ToArray());
            var result = OrderPayloadCodec.Read(reader);
            reader.EnsureConsumed();
            return result;
        }

        private static readonly Vec3[] TwoPoints = { new Vec3(0f, 0f, 0f), new Vec3(10f, 0.5f, -3f) };
        private static readonly PathLink[] OneLink = { new PathLink(1, 4) };

        [Fact]
        public void Write_MinimalOrder_ProducesExactBytes()
        {
            // The one byte-exact wire-format regression test. If this changes,
            // PbjProtocol.Version must change with it.
            var writer = new PbjWriter();
            OrderPayloadCodec.Write(writer, new OrderPayload("m", "u", 0f, 0f));

            var expected = new byte[]
            {
                0x01, 0x00, 0x00, 0x00, 0x6D,       // blueprint "m"
                0x01, 0x00, 0x00, 0x00, 0x75,       // ownerName "u"
                0x00, 0x00, 0x00, 0x00,             // startTime 0f
                0x00, 0x00, 0x00, 0x00,             // duration 0f
                0x00,                               // targetedPoint absent
                0x00,                               // targetedDirection absent
                0xFF, 0xFF, 0xFF, 0xFF,             // targetedEntityName null
                0xFF, 0xFF, 0xFF, 0xFF,             // targetedSocketName null
                0xFF, 0xFF, 0xFF, 0xFF,             // targetedHardpointName null
                0x00, 0x00, 0x00, 0x00,             // pathPoints count 0
                0x00, 0x00, 0x00, 0x00,             // pathLinks count 0
                0x00,                               // directionInterpolant absent
                0x00,                               // offsetInterpolant absent
                0x00,                               // melee absent
                0x00,                               // dash absent
                0x00,                               // dashVertical absent
                0x00, 0x00, 0x00, 0x00,             // dashBulldoze count 0
            };
            Assert.Equal(expected, writer.ToArray());
        }

        [Fact]
        public void RoundTrip_MinimalMoveOrder_PreservesAllFields()
        {
            var result = RoundTrip(new OrderPayload("move_run", "unit_a", 1.25f, 2.5f));
            Assert.Equal("move_run", result.Blueprint);
            Assert.Equal("unit_a", result.OwnerName);
            Assert.Equal(1.25f, result.StartTime);
            Assert.Equal(2.5f, result.Duration);
        }

        [Fact]
        public void RoundTrip_WithNullOptionalFields_PreservesNulls()
        {
            var result = RoundTrip(new OrderPayload("wait", "unit_b", 0f, 1f));
            Assert.Null(result.TargetedPoint);
            Assert.Null(result.TargetedDirection);
            Assert.Null(result.TargetedEntityName);
            Assert.Null(result.TargetedSocketName);
            Assert.Null(result.TargetedHardpointName);
            Assert.Null(result.DirectionInterpolant);
            Assert.Null(result.OffsetInterpolant);
            Assert.Null(result.Melee);
            Assert.Null(result.Dash);
            Assert.Null(result.DashVertical);
            Assert.Empty(result.PathPoints);
            Assert.Empty(result.PathLinks);
            Assert.Empty(result.DashBulldoze);
        }

        [Fact]
        public void RoundTrip_FullyPopulatedOrder_PreservesAllFields()
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
                dashBulldoze: new[]
                {
                    new DestructionPoint(0.5f, new Vec3(7f, 8f, 9f), 3),
                    new DestructionPoint(1.5f, new Vec3(1f, 1f, 1f), 4),
                });

            var result = RoundTrip(order);

            Assert.Equal("melee_charge", result.Blueprint);
            Assert.Equal("unit_z", result.OwnerName);
            Assert.Equal(new Vec3(1f, 2f, 3f), result.TargetedPoint);
            Assert.Equal(new Vec3(0f, 1f, 0f), result.TargetedDirection);
            Assert.Equal("unit_enemy", result.TargetedEntityName);
            Assert.Equal("torso", result.TargetedSocketName);
            Assert.Equal("hp_main", result.TargetedHardpointName);
            Assert.Equal(TwoPoints, result.PathPoints);
            Assert.Equal(OneLink, result.PathLinks);
            Assert.Equal(0.5f, result.DirectionInterpolant);
            Assert.Equal(0.25f, result.OffsetInterpolant);
            Assert.True(result.Melee!.IsOffsetRight);
            Assert.Equal("shock_a", result.Melee.ShockwaveKey);
            Assert.True(result.Melee.PartUsed);
            Assert.Equal(0.4f, result.Dash!.DurationDashOut);
            Assert.Equal(0.6f, result.Dash.DurationDashAlign);
            Assert.Equal(new Vec3(4f, 5f, 6f), result.DashVertical!.Origin);
            Assert.Equal(12f, result.DashVertical.Altitude);
            Assert.Equal(2, result.DashBulldoze.Count);
            Assert.Equal(1.5f, result.DashBulldoze[1].Time);
            Assert.Equal(new Vec3(1f, 1f, 1f), result.DashBulldoze[1].Position);
            Assert.Equal(4, result.DashBulldoze[1].Index);
        }

        [Fact]
        public void RoundTrip_WithMeleeShockwaveKeyNull_PreservesNull()
        {
            var order = new OrderPayload("melee", "u", 0f, 1f, melee: new MeleePayload(false, null, false));
            Assert.Null(RoundTrip(order).Melee!.ShockwaveKey);
        }

        [Fact]
        public void RoundTrip_WithLongMovementPath_PreservesPointsAndLinks()
        {
            var points = Enumerable.Range(0, 128).Select(i => new Vec3(i, i * 0.5f, -i)).ToArray();
            var links = Enumerable.Range(0, 127).Select(i => new PathLink(i % 3, i)).ToArray();
            var result = RoundTrip(new OrderPayload("move_run", "u", 0f, 5f, pathPoints: points, pathLinks: links));
            Assert.Equal(128, result.PathPoints.Count);
            Assert.Equal(127, result.PathLinks.Count);
            Assert.Equal(new Vec3(127, 63.5f, -127), result.PathPoints[127]);
            Assert.Equal(126, result.PathLinks[126].DestinationIndex);
        }

        // --- decode guards ---

        [Fact]
        public void Read_WithTooManyPathPoints_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteString("move_run");
            writer.WriteString("unit_a");
            writer.WriteSingle(0f);
            writer.WriteSingle(0f);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteInt32(OrderPayload.MaxPathPoints + 1);
            Assert.Throws<PbjProtocolException>(() => OrderPayloadCodec.Read(new PbjReader(writer.ToArray())));
        }

        [Fact]
        public void Read_WithNegativeCount_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteString("move_run");
            writer.WriteString("unit_a");
            writer.WriteSingle(0f);
            writer.WriteSingle(0f);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteInt32(-1);
            Assert.Throws<PbjProtocolException>(() => OrderPayloadCodec.Read(new PbjReader(writer.ToArray())));
        }

        [Fact]
        public void Read_TruncatedPayload_Throws()
        {
            var writer = new PbjWriter();
            OrderPayloadCodec.Write(writer, new OrderPayload("move_run", "unit_a", 0f, 1f));
            var full = writer.ToArray();
            var truncated = new byte[full.Length - 6];
            System.Array.Copy(full, truncated, truncated.Length);
            Assert.Throws<PbjProtocolException>(() => OrderPayloadCodec.Read(new PbjReader(truncated)));
        }

        [Fact]
        public void Read_WithBlankBlueprint_ThrowsProtocolException()
        {
            // A hostile peer can send anything, so OrderPayload's own ctor guard
            // must surface as a protocol error rather than an ArgumentException.
            // Written field-by-field because the guard makes the payload
            // unconstructible through the normal path.
            var writer = new PbjWriter();
            writer.WriteString("   ");
            writer.WriteString("unit_a");
            writer.WriteSingle(0f);
            writer.WriteSingle(0f);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteString(null);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteInt32(0);

            Assert.Throws<PbjProtocolException>(() => OrderPayloadCodec.Read(new PbjReader(writer.ToArray())));
        }

        [Fact]
        public void Write_WithNullOrder_Throws()
        {
            var ex = Assert.Throws<System.ArgumentNullException>(
                () => OrderPayloadCodec.Write(new PbjWriter(), null!));
            Assert.Equal("order", ex.ParamName);
        }

        [Fact]
        public void Write_WithNullWriter_Throws()
        {
            var ex = Assert.Throws<System.ArgumentNullException>(
                () => OrderPayloadCodec.Write(null!, new OrderPayload("m", "u", 0f, 0f)));
            Assert.Equal("writer", ex.ParamName);
        }

        [Fact]
        public void Read_WithNullReader_Throws()
        {
            var ex = Assert.Throws<System.ArgumentNullException>(() => OrderPayloadCodec.Read(null!));
            Assert.Equal("reader", ex.ParamName);
        }

        [Fact]
        public void RoundTrip_IsCultureIndependent()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var result = RoundTrip(new OrderPayload("move_run", "u", 1.5f, 2.25f));
                Assert.Equal(1.5f, result.StartTime);
                Assert.Equal(2.25f, result.Duration);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    }
}
