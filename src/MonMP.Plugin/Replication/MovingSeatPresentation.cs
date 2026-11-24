using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class MovingSeatPresentation
    {
        internal static void Follow(Vector3 oldPosition, Quaternion oldRotation, Transform seat, Transform avatar)
        {
            var position = seat.position;
            var rotation = seat.rotation;
            // A parked seat contributes no late motion. Compare components exactly so even
            // tiny native movement still follows, and leave any external avatar motion alone.
            if (position.x == oldPosition.x && position.y == oldPosition.y && position.z == oldPosition.z &&
                rotation.x == oldRotation.x && rotation.y == oldRotation.y && rotation.z == oldRotation.z && rotation.w == oldRotation.w) return;
            var turn = rotation * Quaternion.Inverse(oldRotation);
            avatar.SetPositionAndRotation(position + turn * (avatar.position - oldPosition), turn * avatar.rotation);
        }
    }
}
