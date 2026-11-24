using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Both host command reception and guest world reception use the same 10 Hz pose track.
    internal sealed class RemoteToolPose
    {
        private Vector3 from, target;
        private Quaternion fromRotation, targetRotation;
        private float received;
        private bool known;
        internal void Reset() => known = false;
        internal void Receive(Vector3 position, Quaternion rotation, float now, bool newOwner)
        {
            Sample(now, out var shown, out var shownRotation);
            bool snap = !known || newOwner || now - received > 0.5f || (position - target).sqrMagnitude > 9f;
            from = snap ? position : shown; fromRotation = snap ? rotation : shownRotation;
            target = position; targetRotation = rotation; received = now; known = true;
        }
        internal void Sample(float now, out Vector3 position, out Quaternion rotation)
        {
            float t = Mathf.Clamp01((now - received) / 0.1f);
            position = Vector3.Lerp(from, target, t); rotation = Quaternion.Slerp(fromRotation, targetRotation, t);
        }
    }
}
