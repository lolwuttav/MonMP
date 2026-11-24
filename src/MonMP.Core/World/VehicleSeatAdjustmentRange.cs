using System;

namespace MonMP.Core.World
{
    public static class VehicleSeatAdjustmentRange
    {
        public static bool TryCentimeters(float meters, out short centimeters)
        {
            centimeters = 0;
            double rounded = Math.Round((double)meters * 100.0, MidpointRounding.AwayFromZero);
            if (double.IsNaN(rounded) || double.IsInfinity(rounded) || rounded < short.MinValue || rounded > short.MaxValue) return false;
            centimeters = (short)rounded;
            return true;
        }

        // Snapshot positions and native endpoints must use the same quantization.
        // Native LoadFromSaveSystem clamps the decoded value to the exact float limits.
        public static bool TryLimits(float minimum, float maximum, out short min, out short max)
        {
            min = max = 0;
            return minimum <= maximum && TryCentimeters(minimum, out min) && TryCentimeters(maximum, out max);
        }
    }
}
