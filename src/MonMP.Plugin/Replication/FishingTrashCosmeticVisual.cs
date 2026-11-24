using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Follows sampled native drop motion without a second physics simulation. SharedItems
    // ignores this marker so the visual never receives a world entity or lease.
    internal sealed class FishingTrashCosmeticVisual : MonoBehaviour
    {
        private Vector3 from, target;
        private Quaternion fromRotation, targetRotation;
        private float elapsed;
        private bool moving, hasTarget;

        internal void MoveTo(ItemPose pose)
        {
            // Periodic snapshots and other anglers' updates repeat this same pose.
            // They must not restart interpolation toward an already accepted target.
            if (hasTarget && target.x == pose.X && target.y == pose.Y && target.z == pose.Z &&
                targetRotation.x == pose.Qx && targetRotation.y == pose.Qy && targetRotation.z == pose.Qz && targetRotation.w == pose.Qw) return;
            from = transform.position; fromRotation = transform.rotation;
            target = new Vector3(pose.X, pose.Y, pose.Z);
            targetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            elapsed = 0f; moving = hasTarget = true;
        }

        private void Update()
        {
            if (!moving) return;
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / 0.25f);
            transform.position = Vector3.Lerp(from, target, t);
            transform.rotation = Quaternion.Slerp(fromRotation, targetRotation, t);
            if (t >= 1f) moving = false;
        }
    }
}
