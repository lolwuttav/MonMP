using System;

namespace MonMP.Core.World
{
    // A consecutive quiet interval, in game time. Bad samples restart settling;
    // no timer runs while a dropped item is falling, sliding or being carried.
    public sealed class CargoSettleWindow
    {
        public const float SettleSeconds = 1f;
        private float quietSince = -1, lastAt = -1;
        public bool Observe(float now, bool supportedAndContained, float relativeSpeedSquared,
            float relativeSpinSquared, float localDriftSquared, float localAngle)
        {
            bool quiet = supportedAndContained && Finite(now) &&
                IsMotionQuiet(relativeSpeedSquared, relativeSpinSquared, localDriftSquared, localAngle);
            if (!quiet || lastAt >= 0 && (now < lastAt || now - lastAt > 0.3f)) quietSince = -1;
            lastAt = now;
            if (!quiet) return false;
            if (quietSince < 0) quietSince = now;
            return now - quietSince >= SettleSeconds;
        }
        // Callers can skip native hull/support queries when motion already rules
        // out this sample. Observe still owns all timing/reset decisions.
        public static bool IsMotionQuiet(float relativeSpeedSquared, float relativeSpinSquared, float localDriftSquared, float localAngle) =>
            Finite(relativeSpeedSquared) && Finite(relativeSpinSquared) && Finite(localDriftSquared) && Finite(localAngle) &&
            relativeSpeedSquared >= 0 && relativeSpeedSquared <= 0.15f * 0.15f &&
            relativeSpinSquared >= 0 && relativeSpinSquared <= 0.35f * 0.35f &&
            localDriftSquared >= 0 && localDriftSquared <= 0.015f * 0.015f && localAngle >= 0 && localAngle <= 1.5f;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
