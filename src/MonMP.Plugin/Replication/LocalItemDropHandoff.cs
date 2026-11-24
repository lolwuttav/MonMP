namespace MonMP.Plugin.Replication
{
    // Pending and acknowledged drops retain one collision-checked falling trajectory.
    internal static class LocalItemDropHandoff
    {
        // The debug 500 ms setting delays both directions: a release plus its
        // falling/settled host samples can take well over one second to arrive.
        // Retain the collision-checked pose through that bounded round trip;
        // this does not extend speculative movement or ownership.
        internal const float MaximumAge = 2f;
        internal const float FollowRate = 18f;

        // Both distances are signed metres along gravity: canonicalDescent is the
        // host's change between samples, shownLead is how far our copy is ahead.
        // The caller owns lease, geometry, carrier-space and correction eligibility.
        internal static bool Keep(float age, bool canPredict, bool hasMotion, float canonicalDescent, float shownLead, float speed = 0)
        {
            if (!Finite(age) || !Finite(canonicalDescent) || !Finite(shownLead) || !Finite(speed) || speed < 0 || age < 0 ||
                age >= MaximumAge || canonicalDescent < -0.001f) return false;
            // An equal host position still produces zero speed in ordinary follow.
            // Wait for enough target lead to sustain this frame's falling speed.
            return hasMotion ? shownLead > -speed / FollowRate : canPredict;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
