using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Binary codec for <see cref="OrderPayload"/>. The field order here is the
    /// wire format; changing it requires a <see cref="PbjProtocol.Version"/> bump
    /// and will break <c>Write_MinimalOrder_ProducesExactBytes</c> on purpose.
    /// </summary>
    public static class OrderPayloadCodec
    {
        public static void Write(PbjWriter writer, OrderPayload order)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            writer.WriteString(order.Blueprint);
            writer.WriteString(order.OwnerName);
            writer.WriteSingle(order.StartTime);
            writer.WriteSingle(order.Duration);
            WriteOptionalVec3(writer, order.TargetedPoint);
            WriteOptionalVec3(writer, order.TargetedDirection);
            writer.WriteString(order.TargetedEntityName);
            writer.WriteString(order.TargetedSocketName);
            writer.WriteString(order.TargetedHardpointName);

            writer.WriteInt32(order.PathPoints.Count);
            for (var i = 0; i < order.PathPoints.Count; i++)
            {
                WriteVec3(writer, order.PathPoints[i]);
            }

            writer.WriteInt32(order.PathLinks.Count);
            for (var i = 0; i < order.PathLinks.Count; i++)
            {
                writer.WriteInt32(order.PathLinks[i].Type);
                writer.WriteInt32(order.PathLinks[i].DestinationIndex);
            }

            WriteOptionalSingle(writer, order.DirectionInterpolant);
            WriteOptionalSingle(writer, order.OffsetInterpolant);

            if (order.Melee == null)
            {
                writer.WriteBool(false);
            }
            else
            {
                writer.WriteBool(true);
                writer.WriteBool(order.Melee.IsOffsetRight);
                writer.WriteString(order.Melee.ShockwaveKey);
                writer.WriteBool(order.Melee.PartUsed);
            }

            if (order.Dash == null)
            {
                writer.WriteBool(false);
            }
            else
            {
                writer.WriteBool(true);
                writer.WriteSingle(order.Dash.DurationDashOut);
                writer.WriteSingle(order.Dash.DurationDashAlign);
            }

            if (order.DashVertical == null)
            {
                writer.WriteBool(false);
            }
            else
            {
                writer.WriteBool(true);
                WriteVec3(writer, order.DashVertical.Origin);
                writer.WriteSingle(order.DashVertical.Altitude);
            }

            writer.WriteInt32(order.DashBulldoze.Count);
            for (var i = 0; i < order.DashBulldoze.Count; i++)
            {
                writer.WriteSingle(order.DashBulldoze[i].Time);
                WriteVec3(writer, order.DashBulldoze[i].Position);
                writer.WriteInt32(order.DashBulldoze[i].Index);
            }
        }

        public static OrderPayload Read(PbjReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var blueprint = reader.ReadString();
            var ownerName = reader.ReadString();
            var startTime = reader.ReadSingle();
            var duration = reader.ReadSingle();
            var targetedPoint = ReadOptionalVec3(reader);
            var targetedDirection = ReadOptionalVec3(reader);
            var targetedEntityName = reader.ReadString();
            var targetedSocketName = reader.ReadString();
            var targetedHardpointName = reader.ReadString();

            var pointCount = ReadCount(reader, "path point");
            var points = new Vec3[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                points[i] = ReadVec3(reader);
            }

            var linkCount = ReadCount(reader, "path link");
            var links = new PathLink[linkCount];
            for (var i = 0; i < linkCount; i++)
            {
                links[i] = new PathLink(reader.ReadInt32(), reader.ReadInt32());
            }

            var directionInterpolant = ReadOptionalSingle(reader);
            var offsetInterpolant = ReadOptionalSingle(reader);

            MeleePayload? melee = null;
            if (reader.ReadBool())
            {
                melee = new MeleePayload(reader.ReadBool(), reader.ReadString(), reader.ReadBool());
            }

            DashPayload? dash = null;
            if (reader.ReadBool())
            {
                dash = new DashPayload(reader.ReadSingle(), reader.ReadSingle());
            }

            DashVerticalPayload? dashVertical = null;
            if (reader.ReadBool())
            {
                dashVertical = new DashVerticalPayload(ReadVec3(reader), reader.ReadSingle());
            }

            var bulldozeCount = ReadCount(reader, "destruction point");
            var bulldoze = new DestructionPoint[bulldozeCount];
            for (var i = 0; i < bulldozeCount; i++)
            {
                bulldoze[i] = new DestructionPoint(reader.ReadSingle(), ReadVec3(reader), reader.ReadInt32());
            }

            try
            {
                return new OrderPayload(
                    blueprint!, ownerName!, startTime, duration,
                    targetedPoint, targetedDirection,
                    targetedEntityName, targetedSocketName, targetedHardpointName,
                    points, links,
                    directionInterpolant, offsetInterpolant,
                    melee, dash, dashVertical, bulldoze);
            }
            catch (ArgumentException e)
            {
                // A peer can put anything on the wire, so the payload's own
                // invariants are a protocol matter here, not a caller bug.
                throw new PbjProtocolException("Malformed order payload: " + e.Message);
            }
        }

        private static int ReadCount(PbjReader reader, string what)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > OrderPayload.MaxPathPoints)
            {
                throw new PbjProtocolException(
                    "Order declares " + count + " " + what + "(s), outside 0.."
                    + OrderPayload.MaxPathPoints + ".");
            }
            return count;
        }

        private static void WriteVec3(PbjWriter writer, Vec3 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
        }

        private static Vec3 ReadVec3(PbjReader reader)
        {
            return new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteOptionalVec3(PbjWriter writer, Vec3? value)
        {
            if (value.HasValue)
            {
                writer.WriteBool(true);
                WriteVec3(writer, value.Value);
            }
            else
            {
                writer.WriteBool(false);
            }
        }

        private static Vec3? ReadOptionalVec3(PbjReader reader)
        {
            return reader.ReadBool() ? ReadVec3(reader) : (Vec3?)null;
        }

        private static void WriteOptionalSingle(PbjWriter writer, float? value)
        {
            if (value.HasValue)
            {
                writer.WriteBool(true);
                writer.WriteSingle(value.Value);
            }
            else
            {
                writer.WriteBool(false);
            }
        }

        private static float? ReadOptionalSingle(PbjReader reader)
        {
            return reader.ReadBool() ? reader.ReadSingle() : (float?)null;
        }
    }
}
