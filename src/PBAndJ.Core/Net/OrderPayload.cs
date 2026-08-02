using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>A position or direction on the wire. Mirrors UnityEngine.Vector3.</summary>
    public readonly struct Vec3
    {
        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
    }

    /// <summary>One navigation link along a movement path. Mirrors Area.AreaNavLink.</summary>
    public readonly struct PathLink
    {
        public PathLink(int type, int destinationIndex)
        {
            Type = type;
            DestinationIndex = destinationIndex;
        }

        public int Type { get; }
        public int DestinationIndex { get; }
    }

    /// <summary>
    /// One predicted terrain destruction during a bulldoze dash.
    /// Mirrors PhantomBrigade.Combat.PointDestructionPrediction.
    /// </summary>
    public readonly struct DestructionPoint
    {
        public DestructionPoint(float time, Vec3 position, int index)
        {
            Time = time;
            Position = position;
            Index = index;
        }

        public float Time { get; }
        public Vec3 Position { get; }
        public int Index { get; }
    }

    /// <summary>Melee specifics. Mirrors DataBlockSavedActionMelee.</summary>
    public sealed class MeleePayload
    {
        public MeleePayload(bool isOffsetRight, string? shockwaveKey, bool partUsed)
        {
            IsOffsetRight = isOffsetRight;
            ShockwaveKey = shockwaveKey;
            PartUsed = partUsed;
        }

        public bool IsOffsetRight { get; }
        public string? ShockwaveKey { get; }
        public bool PartUsed { get; }
    }

    /// <summary>Dash timings. Mirrors DataBlockSavedActionDash.</summary>
    public sealed class DashPayload
    {
        public DashPayload(float durationDashOut, float durationDashAlign)
        {
            DurationDashOut = durationDashOut;
            DurationDashAlign = durationDashAlign;
        }

        public float DurationDashOut { get; }
        public float DurationDashAlign { get; }
    }

    /// <summary>Vertical dash specifics. Mirrors the MovementDashVertical component.</summary>
    public sealed class DashVerticalPayload
    {
        public DashVerticalPayload(Vec3 origin, float altitude)
        {
            Origin = origin;
            Altitude = altitude;
        }

        public Vec3 Origin { get; }
        public float Altitude { get; }
    }

    /// <summary>
    /// One planned combat order, in the form it travels over the wire.
    /// Mirrors <c>DataContainerSavedAction</c> field-for-field.
    /// </summary>
    /// <remarks>
    /// Core-owned rather than the game's own type because Core must stay free of
    /// Assembly-CSharp and UnityEngine — see docs/design/networking.md for the
    /// full argument. <c>OrderMapper</c> in PBAndJ.Mod bridges the two.
    /// <para>
    /// <c>dashBulldoze</c> is carried, not dropped: nothing in the game
    /// recomputes it, and it drives terrain destruction and a dash stability
    /// bonus during execution.
    /// </para>
    /// </remarks>
    public sealed class OrderPayload
    {
        /// <summary>Cap on any per-order list, guarding against a hostile peer.</summary>
        public const int MaxPathPoints = 512;

        private static readonly Vec3[] NoPoints = new Vec3[0];
        private static readonly PathLink[] NoLinks = new PathLink[0];
        private static readonly DestructionPoint[] NoDestruction = new DestructionPoint[0];

        public OrderPayload(
            string blueprint,
            string ownerName,
            float startTime,
            float duration,
            Vec3? targetedPoint = null,
            Vec3? targetedDirection = null,
            string? targetedEntityName = null,
            string? targetedSocketName = null,
            string? targetedHardpointName = null,
            IReadOnlyList<Vec3>? pathPoints = null,
            IReadOnlyList<PathLink>? pathLinks = null,
            float? directionInterpolant = null,
            float? offsetInterpolant = null,
            MeleePayload? melee = null,
            DashPayload? dash = null,
            DashVerticalPayload? dashVertical = null,
            IReadOnlyList<DestructionPoint>? dashBulldoze = null)
        {
            if (string.IsNullOrWhiteSpace(blueprint))
            {
                throw new ArgumentException("Order blueprint must be a non-empty string.", nameof(blueprint));
            }
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                throw new ArgumentException("Order owner name must be a non-empty string.", nameof(ownerName));
            }

            var points = pathPoints ?? NoPoints;
            var links = pathLinks ?? NoLinks;
            var bulldoze = dashBulldoze ?? NoDestruction;

            if (points.Count > MaxPathPoints)
            {
                throw new ArgumentException(
                    "Path has " + points.Count + " points, more than the maximum of " + MaxPathPoints + ".",
                    nameof(pathPoints));
            }
            if (links.Count > MaxPathPoints)
            {
                throw new ArgumentException(
                    "Path has " + links.Count + " links, more than the maximum of " + MaxPathPoints + ".",
                    nameof(pathLinks));
            }
            if (bulldoze.Count > MaxPathPoints)
            {
                throw new ArgumentException(
                    "Dash has " + bulldoze.Count + " destruction points, more than the maximum of "
                    + MaxPathPoints + ".",
                    nameof(dashBulldoze));
            }

            // ActionUtility.CreatePathAction accepts any path with >= 2 points
            // and >= 1 link, and imposes no relationship between the two counts.
            // Mirror that exactly rather than inventing a stricter rule.
            if (points.Count > 0 || links.Count > 0)
            {
                if (points.Count < 2)
                {
                    throw new ArgumentException(
                        "A movement path needs at least two points.", nameof(pathPoints));
                }
                if (links.Count < 1)
                {
                    throw new ArgumentException(
                        "A movement path needs at least one link.", nameof(pathLinks));
                }
            }

            Blueprint = blueprint;
            OwnerName = ownerName;
            StartTime = startTime;
            Duration = duration;
            TargetedPoint = targetedPoint;
            TargetedDirection = targetedDirection;
            TargetedEntityName = targetedEntityName;
            TargetedSocketName = targetedSocketName;
            TargetedHardpointName = targetedHardpointName;
            PathPoints = points;
            PathLinks = links;
            DirectionInterpolant = directionInterpolant;
            OffsetInterpolant = offsetInterpolant;
            Melee = melee;
            Dash = dash;
            DashVertical = dashVertical;
            DashBulldoze = bulldoze;
        }

        public string Blueprint { get; }
        public string OwnerName { get; }
        public float StartTime { get; }
        public float Duration { get; }
        public Vec3? TargetedPoint { get; }
        public Vec3? TargetedDirection { get; }
        public string? TargetedEntityName { get; }
        public string? TargetedSocketName { get; }
        public string? TargetedHardpointName { get; }
        public IReadOnlyList<Vec3> PathPoints { get; }
        public IReadOnlyList<PathLink> PathLinks { get; }
        public float? DirectionInterpolant { get; }
        public float? OffsetInterpolant { get; }
        public MeleePayload? Melee { get; }
        public DashPayload? Dash { get; }
        public DashVerticalPayload? DashVertical { get; }
        public IReadOnlyList<DestructionPoint> DashBulldoze { get; }
    }
}
