using System;

namespace MonMP.Plugin.Replication
{
    // Shares a periodic pass over a list across rendered frames: each frame takes the part
    // of the list that the elapsed time has earned, so every entry is still visited once
    // per period. A frame as long as the period (or a clock restart) takes the whole list.
    internal static class ItemCaptureSlices
    {
        internal static int Take(ref float credit, int total, float elapsed, float period)
        {
            if (total <= 0) { credit = 0f; return 0; }
            if (!(elapsed >= 0f) || elapsed >= period) { credit = 0f; return total; }
            credit += total * (elapsed / period);
            int count = Math.Min((int)credit, total);
            credit = Math.Min(credit - count, 1f);
            return count;
        }
    }
}
