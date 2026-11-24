using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteWheelSuspension
    {
        internal static void PresentUngrounded(WheelController controller, float dt, float previousLength)
        {
            if (!controller.visualOnlyUpdate || controller.isGrounded || controller.wheel == null ||
                controller.spring == null || controller.wheelHit == null || dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            var wheel = controller.wheel;
            if (wheel.Visual == null) return;
            // HitUpdate leaves the last world-space contact cached when no ray hits. VisualUpdate
            // consumes it anyway, unlike native SuspensionUpdate's no-hit extension. After a
            // downward teleport that old point can be above the mount and collapse length to zero.
            float length = Mathf.Lerp(previousLength, controller.spring.maxLength, dt * 8f);
            var hit = controller.wheelHit.raycastHit;
            hit.point = controller.transform.position - controller.transform.up * length - wheel.inside * wheel.rimOffset;
            controller.wheelHit.raycastHit = hit;
            // This is only a visual fallback: hasHit stays false. Native VisualUpdate preserves the
            // mesh offsets and the nonrotating visual while placing the wheel at this current pose.
            controller.VisualUpdate();
        }
    }
}
