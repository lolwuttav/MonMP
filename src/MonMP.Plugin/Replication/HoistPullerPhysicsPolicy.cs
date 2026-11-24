namespace MonMP.Plugin.Replication
{
    // Anchors and cable shortening come from the host; force belongs to the one
    // machine currently simulating the target vehicle.
    internal static class HoistPullerPhysicsPolicy
    {
        internal static bool CanApplyForce(bool running, bool worldSynchronized, bool attached,
            bool localSimulator, bool dynamicBody) =>
            running && worldSynchronized && attached && localSimulator && dynamicBody;

        // Match the native Tool_HoistPuller.FixedUpdate's two clamped Mathf.Lerp
        // operations. Distance is deliberately the native blend input, not a
        // newly normalized distance between MinRange and MaxRange.
        internal static float ForceMagnitude(float distance, float minRange, float maxRange,
            float minForce, float maxForce)
        {
            if (!Finite(distance) || !Finite(minRange) || !Finite(maxRange) ||
                !Finite(minForce) || !Finite(maxForce) || distance <= minRange ||
                minRange < 0f || maxRange <= 0f || maxRange < minRange ||
                minForce < 0f || maxForce < minForce) return 0f;
            float blend = Lerp(minRange, maxRange, distance) / maxRange;
            return Lerp(minForce, maxForce, blend);
        }

        private static float Lerp(float start, float end, float blend) =>
            start + (end - start) * (blend < 0f ? 0f : blend > 1f ? 1f : blend);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
