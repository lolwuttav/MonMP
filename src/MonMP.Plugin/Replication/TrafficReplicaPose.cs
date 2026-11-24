using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class TrafficReplicaPose
    {
        internal static void Apply(Rigidbody body, Transform root, Vector3 position, Quaternion rotation)
        {
            // The network pose is already interpolated in Update. The native traffic
            // prefabs also enable physics interpolation, which otherwise leaves the
            // rendered car behind the collider that can hit the local driver's car.
            body.interpolation = RigidbodyInterpolation.None;
            body.position = position; body.rotation = rotation;
            root.SetPositionAndRotation(position, rotation);
        }
    }
}
