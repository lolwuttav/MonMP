using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class SleepingBedPose
    {
        // SleepPosition is the native walker root. Its tilted up axis reaches the head.
        internal static Vector3 Eyes(Transform sleepPosition) => sleepPosition.position + sleepPosition.up * 1.6f;
    }
}
