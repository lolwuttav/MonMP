using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native Rigidbody setters cross into physics even when the requested value is unchanged.
    // Keep the original transition order while avoiding repeated writes on resting decorations.
    internal static class ItemBodyConfiguration
    {
        internal static void Apply(Rigidbody body, Collider collider, bool kinematic, bool trigger)
        {
            if (kinematic && body.collisionDetectionMode != CollisionDetectionMode.ContinuousSpeculative)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            if (body.isKinematic != kinematic) body.isKinematic = kinematic;
            if (!kinematic && body.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var interpolation = kinematic ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
            if (body.interpolation != interpolation) body.interpolation = interpolation;
            if (collider.isTrigger != trigger) collider.isTrigger = trigger;
        }
    }
}
