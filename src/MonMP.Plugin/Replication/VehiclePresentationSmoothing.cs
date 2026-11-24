using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class VehiclePresentationSmoothing
    {
        private const float FollowRate = 18f;
        private const float SettleDegrees = 0.1f;
        internal const float PositionSettleDistance = 0.001f;
        internal const float RotationSettleDegrees = 0.1f;
        private const float DiscontinuityDegrees = 120f;
        private const float DiscontinuityDistance = 0.75f;

        internal static float Blend => 1 - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime);

        internal static float Angle(float current, float target, float blend)
        {
            float next = Mathf.LerpAngle(current, target, blend);
            return Mathf.Abs(Mathf.DeltaAngle(next, target)) < SettleDegrees ? target : next;
        }

        internal static Vector3 Position(Vector3 current, Vector3 target, float blend)
        {
            Vector3 next = Vector3.Lerp(current, target, blend);
            return Vector3.Distance(next, target) < PositionSettleDistance ? target : next;
        }

        internal static Quaternion Rotation(Quaternion current, Quaternion target, float blend)
        {
            Quaternion next = Quaternion.Slerp(current, target, blend);
            return Quaternion.Angle(next, target) < RotationSettleDegrees ? target : next;
        }

        internal static bool IsLargeAngleDiscontinuity(float current, float target) =>
            Mathf.Abs(Mathf.DeltaAngle(current, target)) > DiscontinuityDegrees;

        internal static bool IsLargeTransformDiscontinuity(Transform? current, ItemPose target)
        {
            if (current == null) return false;
            return Vector3.Distance(current.localPosition, new Vector3(target.X, target.Y, target.Z)) > DiscontinuityDistance ||
                Quaternion.Angle(current.localRotation, new Quaternion(target.Qx, target.Qy, target.Qz, target.Qw)) > DiscontinuityDegrees;
        }
    }
}
