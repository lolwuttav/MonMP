using System;

namespace MonMP.Plugin.Replication
{
    // Native MovingWindow advances a linear interpolation at 0.35 per scaled second.
    // A separate clock bounds unconfirmed presentation even when the game is paused.
    internal sealed class WindowTravelPresentation
    {
        internal const float TravelPerSecond = 0.35f;
        internal const float CrankDegreesPerSecond = 170f;
        private float elapsed;
        internal float Progress => Math.Min(1, elapsed * TravelPerSecond);
        internal bool Complete => Progress >= 1;
        internal float Advance(float delta)
        {
            float before = elapsed;
            elapsed = Math.Min(1f / TravelPerSecond, elapsed + Math.Max(0, delta));
            return elapsed - before;
        }
    }
}
