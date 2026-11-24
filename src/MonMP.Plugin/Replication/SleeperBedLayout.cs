using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Sorted bed groups are rebuilt only when sleep membership or the local bed changes.
    internal sealed class SleeperBedLayout<TBed> where TBed : class
    {
        private readonly Dictionary<TBed, List<uint>> groups = new Dictionary<TBed, List<uint>>();

        internal void Clear() => groups.Clear();

        internal void Add(TBed bed, uint peer)
        {
            if (!groups.TryGetValue(bed, out var occupants))
                groups.Add(bed, occupants = new List<uint>());
            occupants.Add(peer);
        }

        internal void Sort()
        {
            foreach (var occupants in groups.Values) occupants.Sort();
        }

        internal (int Slot, int Count) Occupancy(TBed bed, uint peer)
        {
            if (!groups.TryGetValue(bed, out var occupants)) return (0, 1);
            // Find the first matching peer, as List.IndexOf did even if the local entry
            // also appears in the pose table. A fallback self entry belongs only to this query.
            int low = 0, high = occupants.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (occupants[middle] < peer) low = middle + 1;
                else high = middle;
            }
            bool present = low < occupants.Count && occupants[low] == peer;
            return (low, occupants.Count + (present ? 0 : 1));
        }
    }
}
