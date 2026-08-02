using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Which player controls which units, for one combat.
    /// </summary>
    /// <remarks>
    /// <see cref="IsOwnedBy"/> is the host-side security boundary: every order in
    /// an inbound <see cref="ReadyMessage"/> is checked against it. Clients also
    /// grey out units they do not own, but that is UX only — never move a check
    /// from the host to the client.
    /// </remarks>
    public sealed class UnitAssignments
    {
        private static readonly string[] NoUnits = new string[0];
        private static readonly int[] NoPeers = new int[0];

        private readonly Dictionary<int, string[]> byPeer;
        private readonly int[] peerIds;

        internal UnitAssignments(Dictionary<int, string[]> byPeer, int[] peerIds)
        {
            this.byPeer = byPeer;
            this.peerIds = peerIds;
        }

        /// <summary>Nobody owns anything — the state before combat starts.</summary>
        public static UnitAssignments Empty { get; } =
            new UnitAssignments(new Dictionary<int, string[]>(), NoPeers);

        /// <summary>Assigned peers, ascending.</summary>
        public IReadOnlyList<int> PeerIds => peerIds;

        public IReadOnlyList<string> UnitsFor(int peerId)
        {
            return byPeer.TryGetValue(peerId, out var units) ? units : NoUnits;
        }

        public bool IsOwnedBy(int peerId, string? unitName)
        {
            if (unitName == null || !byPeer.TryGetValue(peerId, out var units))
            {
                return false;
            }
            for (var i = 0; i < units.Length; i++)
            {
                if (string.Equals(units[i], unitName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The default assignment policy: sort unit names ordinal, then deal them
    /// round-robin across peers in id order, host first.
    /// </summary>
    /// <remarks>
    /// Deterministic and re-derivable, so the same inputs always produce the same
    /// split and a dropped peer can be re-planned without surprising anyone.
    /// <para>
    /// The caller supplies the candidate set, which must be units that are
    /// <c>isPlayerControllable &amp;&amp; IsUnitFriendly</c> — not merely friendly.
    /// Scenario-scripted allies are friendly but AI-driven, and handing one to a
    /// player would put their orders in a fight with the AI planning systems.
    /// </para>
    /// </remarks>
    public static class UnitAssignmentPlanner
    {
        public static UnitAssignments Plan(IReadOnlyList<int> peerIds, IReadOnlyList<string?> unitNames)
        {
            if (peerIds == null)
            {
                throw new ArgumentNullException(nameof(peerIds));
            }
            if (unitNames == null)
            {
                throw new ArgumentNullException(nameof(unitNames));
            }
            if (peerIds.Count == 0)
            {
                throw new ArgumentException("At least one peer is required to assign units.", nameof(peerIds));
            }

            var orderedPeers = new List<int>(peerIds);
            orderedPeers.Sort();

            var names = new List<string>();
            for (var i = 0; i < unitNames.Count; i++)
            {
                var name = unitNames[i];
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name!))
                {
                    names.Add(name!);
                }
            }
            names.Sort(StringComparer.Ordinal);

            var buckets = new Dictionary<int, List<string>>();
            for (var i = 0; i < orderedPeers.Count; i++)
            {
                buckets[orderedPeers[i]] = new List<string>();
            }
            for (var i = 0; i < names.Count; i++)
            {
                buckets[orderedPeers[i % orderedPeers.Count]].Add(names[i]);
            }

            var byPeer = new Dictionary<int, string[]>();
            for (var i = 0; i < orderedPeers.Count; i++)
            {
                byPeer[orderedPeers[i]] = buckets[orderedPeers[i]].ToArray();
            }
            return new UnitAssignments(byPeer, orderedPeers.ToArray());
        }
    }
}
