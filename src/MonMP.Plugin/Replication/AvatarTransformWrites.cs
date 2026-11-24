using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Posing still runs every required frame. Avoid sending an unchanged value back through
    // Unity's Transform setters, including the neutral bones of an otherwise animated body.
    // Check the actual transform, rather than remembering the last requested pose: walking,
    // hand/foot IK and seat alignment are also allowed to change it between these calls.
    internal static class AvatarTransformWrites
    {
        internal static void LocalPosition(Transform target, Vector3 value)
        {
            var actual = target.localPosition;
            if (actual.x != value.x || actual.y != value.y || actual.z != value.z)
                target.localPosition = value;
        }

        internal static void LocalRotation(Transform target, Quaternion value)
        {
            var actual = target.localRotation;
            if (actual.x != value.x || actual.y != value.y || actual.z != value.z || actual.w != value.w)
                target.localRotation = value;
        }

        internal static void LocalScale(Transform target, Vector3 value)
        {
            var actual = target.localScale;
            if (actual.x != value.x || actual.y != value.y || actual.z != value.z)
                target.localScale = value;
        }

        internal static void Pose(Transform[] bones, Quaternion[] rotations, Transform hips, Vector3 hipsPosition)
        {
            for (int i = 0; i < bones.Length; i++) LocalRotation(bones[i], rotations[i]);
            LocalPosition(hips, hipsPosition);
        }
    }
}
