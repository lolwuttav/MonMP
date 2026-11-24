using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(WheelController), nameof(WheelController.OnPrePhysicsSubstep), new[] { typeof(float), typeof(float) })]
    internal static class SharedVehicleWheelVisualPatch
    {
        private static void Prefix(WheelController __instance, out (float Angle, float Spring) __state)
            => __state = (__instance.wheel != null ? __instance.wheel.rotationAngle : 0f, __instance.spring != null ? __instance.spring.length : 0f);

        private static void Postfix(WheelController __instance, float dt, (float Angle, float Spring) __state)
        {
            // Every spin/slip provider rejects physical wheels. Avoid their lookups
            // and visual access on local wheels; the native solver still ran normally.
            // Keep the prefix snapshot: native initialization may change wheel mode.
            if (!__instance.visualOnlyUpdate) return;
            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt) || __instance.wheel == null) return;
            PresentRemoteContact(__instance, dt, __state.Spring);
            var wheel = __instance.wheel;
            var visual = wheel.Visual;
            if (visual == null) return;
            var world = MonMPPlugin.Instance?.World;
            float angularVelocity = 0f;
            bool synchronized = world?.Vehicles?.TryGetRemoteWheelSpin(__instance, out angularVelocity) == true;
            if (!synchronized) synchronized = world?.MartinCar?.TryGetRemoteWheelSpin(__instance, out angularVelocity) == true;
            if (!synchronized) synchronized = world?.SpeedwayEvents?.TryGetRemoteWheelSpin(__instance, out angularVelocity) == true;
            if (!synchronized) return;
            RemoteWheelSuspension.PresentUngrounded(__instance, dt, __state.Spring);

            // NWH's visual-only path estimates speed from wheel-center movement. Use the
            // driver's sampled rad/s instead, preserving its own steer, suspension and mesh offset.
            float nativeAngle = wheel.rotationAngle;
            float synchronizedAngle = Mathf.Repeat(__state.Angle + angularVelocity * Mathf.Rad2Deg * dt, 360f);
            float correction = Mathf.DeltaAngle(nativeAngle, synchronizedAngle);
            wheel.rotationAngle = synchronizedAngle;
            wheel.angularVelocity = angularVelocity;
            visual.transform.rotation = Quaternion.AngleAxis(correction, __instance.transform.right) * visual.transform.rotation;
        }

        // NWH's visual pre-update skips the suspension/contact input refresh. Feed presented shared
        // wheels the driver's slip and chassis motion for tire smoke, skid and bump sounds and marks.
        private static void PresentRemoteContact(WheelController wheel, float dt, float previousSpring)
        {
            var vehicles = MonMPPlugin.Instance?.World?.Vehicles;
            if (vehicles == null || !vehicles.TryGetRemoteWheelSlip(wheel, out float lateral, out float longitudinal, out var vehicle)) return;
            wheel.sideFriction.slip = lateral; wheel.forwardFriction.slip = longitudinal;
            var body = wheel.parentRigidbody;
            bool grounded = wheel.isGrounded;
            float mass = body != null ? body.mass : 1000f;
            int count = vehicle != null && vehicle.Wheels.Count > 0 ? vehicle.Wheels.Count : 4;
            wheel.wheel.load = grounded ? mass * 9.81f / count : 0f;
            if (vehicle != null)
            {
                var velocity = vehicle.Velocity;
                wheel.forwardFriction.speed = grounded ? Vector3.Dot(velocity, wheel.transform.forward) : 0f;
                wheel.sideFriction.speed = grounded ? Vector3.Dot(velocity, wheel.transform.right) : 0f;
            }
            if (wheel.damper != null && wheel.spring != null)
                wheel.damper.force = grounded ? (previousSpring - wheel.spring.length) / dt * Mathf.Max(wheel.damper.bumpForce, wheel.damper.reboundForce) : 0f;
        }
    }
}
