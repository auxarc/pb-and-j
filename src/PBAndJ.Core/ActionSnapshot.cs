using System;

namespace PBAndJ.Core
{
    /// <summary>
    /// Pure-data snapshot of one planned action, captured by the glue layer from
    /// an ActionEntity's components at the execution commit point.
    /// </summary>
    public sealed class ActionSnapshot
    {
        public int OwnerId { get; }
        public string OwnerName { get; }
        public string DataKey { get; }
        public float StartTime { get; }
        public float Duration { get; }
        public bool Locked { get; }

        public ActionSnapshot(int ownerId, string? ownerName, string dataKey, float startTime, float duration, bool locked)
        {
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                throw new ArgumentException("Action data key must be a non-empty string.", nameof(dataKey));
            }
            OwnerId = ownerId;
            OwnerName = string.IsNullOrWhiteSpace(ownerName) ? "?" : ownerName!;
            DataKey = dataKey;
            StartTime = startTime;
            Duration = duration;
            Locked = locked;
        }
    }
}
