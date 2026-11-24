using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeVehicleLiftFreeze
    {
        // Native Local mode always clears isKinematic. Reapply the lift's independent freeze
        // after that transition and release only the freeze this adapter previously applied.
        internal static void Apply(Rigidbody body, bool local, bool frozen, ref bool wasFrozen)
        {
            if (!local || frozen)
            {
                body.collisionDetectionMode = frozen ? CollisionDetectionMode.Discrete : CollisionDetectionMode.ContinuousSpeculative;
                body.isKinematic = true;
            }
            else if (wasFrozen)
            {
                body.isKinematic = false;
                body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            wasFrozen = frozen;
        }
    }
}
