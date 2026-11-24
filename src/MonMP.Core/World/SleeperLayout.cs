using System;

namespace MonMP.Core.World
{
    public readonly struct SleeperLayout
    {
        public float Sideways { get; }
        public bool OnFloor { get; }
        private SleeperLayout(float sideways, bool onFloor) { Sideways = sideways; OnFloor = onFloor; }
        public static SleeperLayout For(int slot, int sleepers, float width, bool curled)
        {
            if (slot < 0 || sleepers <= slot || float.IsNaN(width) || float.IsInfinity(width) || width <= 0)
                throw new ArgumentException("Invalid bed occupancy.");
            int capacity = !curled && width >= 1.25f ? 2 : 1;
            int inBed = Math.Min(sleepers, capacity);
            if (slot < capacity) return new SleeperLayout((slot - (inBed - 1) * 0.5f) * 0.65f, false);
            int extra = slot - capacity;
            // Successive floor rows on alternating sides, all in the lying pose.
            return new SleeperLayout((extra % 2 == 0 ? 1 : -1) * (width * 0.5f + 0.45f + extra / 2 * 0.7f), true);
        }
    }
}
