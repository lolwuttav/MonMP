using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local avatar presentation only. All calibrated coordinates are world metres in an
    // orthonormal seat/wheel frame; native transform scale must not stretch the saved pose.
    internal static class VehicleSeatPosePresentation
    {
        private static readonly Dictionary<Transform, (float Radius, Vector3 LocalCenter)> wheelShapes =
            new Dictionary<Transform, (float, Vector3)>();

        internal static void BodyPlacement(VehicleSeatPoseCatalog.Entry entry, VehicleSeatPoseOverrides.Pose pose,
            out Vector3 position, out Quaternion rotation)
        {
            var anchor = entry.Anchor;
            position = anchor.position + anchor.rotation * new Vector3(pose.BodyX, pose.BodyY, pose.BodyZ);
            rotation = anchor.rotation * Quaternion.Euler(pose.BodyPitch, pose.BodyYaw, pose.BodyRoll);
        }

        internal static void HandFrame(VehicleSeatPoseCatalog.Entry entry, out Vector3 position, out Quaternion rotation)
        {
            HandFrame(entry.Mode == StockAvatarBody.Mode.Drive ? entry.Wheel : null,
                entry.WheelAngle, entry.VehicleRight, entry.Anchor.position, entry.Anchor.rotation,
                out position, out rotation);
        }

        // The preview supplies its captured wheel/seat transforms here, so camera/editor work
        // can use the same math without changing native steering or passenger seat transforms.
        internal static void HandFrame(Transform? wheel, float angle, Vector3 vehicleRight,
            Vector3 seatPosition, Quaternion seatRotation, out Vector3 position, out Quaternion rotation)
        {
            if (wheel != null)
            {
                WheelFrame(wheel, angle, vehicleRight, out position, out rotation, out _);
                return;
            }
            position = seatPosition;
            rotation = seatRotation;
        }

        internal static (Vector3 Left, Vector3 Right) AutomaticWheelGrips(Transform wheel, float angle, Vector3 vehicleRight)
        {
            WheelFrame(wheel, angle, vehicleRight, out var center, out var rotation, out float radius);
            var across = rotation * Vector3.right;
            return (center - across * radius, center + across * radius);
        }

        private static void WheelFrame(Transform wheel, float angle, Vector3 vehicleRight,
            out Vector3 center, out Quaternion rotation, out float radius)
        {
            if (!wheelShapes.TryGetValue(wheel, out var shape))
            {
                shape = (0.17f, Vector3.zero);
                var renderers = wheel.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    shape = (Mathf.Clamp(Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) * 0.45f,
                        0.1f, 0.22f), wheel.InverseTransformPoint(bounds.center));
                }
                wheelShapes[wheel] = shape;
            }
            var axis = wheel.forward;
            var across = vehicleRight - axis * Vector3.Dot(vehicleRight, axis);
            if (across.sqrMagnitude < 1e-4f) across = wheel.right;
            across = Quaternion.AngleAxis(Mathf.Clamp(angle, -100f, 100f), axis) * across.normalized;
            center = wheel.TransformPoint(shape.LocalCenter);
            radius = shape.Radius;
            // LookRotation's right axis is across, matching nine/three o'clock and the bounded
            // steering angle. Saved palm offsets and rotations follow this frame once only.
            rotation = Quaternion.LookRotation(axis, Vector3.Cross(axis, across));
        }

        internal static void ApplyHands(VehicleSeatPoseOverrides.Pose pose, Vector3 framePosition,
            Quaternion frameRotation, ref StockAvatarBody.Goals goals, bool driver = true)
        {
            if (!pose.CustomHands) return;
            var left = framePosition + frameRotation * new Vector3(pose.LeftX, pose.LeftY, pose.LeftZ);
            var right = framePosition + frameRotation * new Vector3(pose.RightX, pose.RightY, pose.RightZ);
            var leftRotation = frameRotation * Quaternion.Euler(pose.LeftPitch, pose.LeftYaw, pose.LeftRoll);
            var rightRotation = frameRotation * Quaternion.Euler(pose.RightPitch, pose.RightYaw, pose.RightRoll);
            if (driver)
            {
                goals.GripLeft = left; goals.GripRight = right; goals.GripCenter = framePosition;
                goals.GripLeftRotation = leftRotation; goals.GripRightRotation = rightRotation;
            }
            else
            {
                // Unlike working wheel grips, these apply only after the passenger's tools,
                // props and gestures have claimed the hands they need for this frame.
                goals.SeatLeft = left; goals.SeatRight = right;
                goals.SeatLeftRotation = leftRotation; goals.SeatRightRotation = rightRotation;
            }
        }

        internal static void ApplyFeet(VehicleSeatPoseOverrides.Pose pose, Vector3 seatPosition,
            Quaternion seatRotation, ref StockAvatarBody.Goals goals)
        {
            if (!pose.CustomFeet) return;
            // Foot bone origins, in world metres along the native seat axes. Wheel steering and
            // calibrated body offsets must not move contacts saved for the floor or pedals.
            goals.SeatFootLeft = seatPosition + seatRotation * new Vector3(pose.LeftFootX, pose.LeftFootY, pose.LeftFootZ);
            goals.SeatFootRight = seatPosition + seatRotation * new Vector3(pose.RightFootX, pose.RightFootY, pose.RightFootZ);
            goals.SeatFootLeftRotation = seatRotation * Quaternion.Euler(pose.LeftFootPitch, pose.LeftFootYaw, pose.LeftFootRoll);
            goals.SeatFootRightRotation = seatRotation * Quaternion.Euler(pose.RightFootPitch, pose.RightFootYaw, pose.RightFootRoll);
        }

        internal static void ForgetWheel(Transform wheel) => wheelShapes.Remove(wheel);
        internal static void Clear() => wheelShapes.Clear();
    }
}
