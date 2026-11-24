using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteSchoolBusPresentation
    {
        internal static void Apply(VehicleController vehicle, bool running)
        {
            if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote) return;
            // A bus loaded asleep has a disabled controller. Remote mode only changes physics;
            // it does not enable Update/FixedUpdate, which supply speed, surfaces and native FX.
            if (!vehicle.enabled) vehicle.enabled = true;
            // Enabling also lets a never-driven bus run its native Start initialization first.
            if (vehicle.powertrain?.Initialized != true) return;
            if (running && !vehicle.IsAwake) vehicle.Wake();
            else if (!running && vehicle.IsAwake) vehicle.Sleep();
            foreach (var component in vehicle.Wheels)
            {
                var wheel = component.wheelController;
                if (wheel == null) continue;
                // NWH's "visual only" flag gates OnPrePhysicsSubstep, but UseInternalUpdate
                // still subscribes its friction/force substeps. Those compare mesh-derived
                // rolling speed with this kinematic body's zero velocity and invent tire slip.
                // Present the bus wheels explicitly after its pose, without those physics calls.
                wheel.visualOnlyUpdate = true;
                if (!wheel.UsingExternalUpdate) wheel.UseExternalUpdate();
                wheel.forwardFriction.slip = wheel.sideFriction.slip = 0f;
                wheel.forwardFriction.force = wheel.sideFriction.force = 0f;
            }
        }

        internal static bool PresentWheels(VehicleController vehicle, Vector3 velocity, float dt, bool snapped)
        {
            if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote ||
                vehicle.powertrain?.Initialized != true || dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return false;
            int count = vehicle.Wheels.Count;
            bool presented = false;
            foreach (var component in vehicle.Wheels)
            {
                var wheel = component.wheelController;
                if (wheel == null) continue;
                // Start reasserts the native multiplayer mode, so also repair subscriptions here.
                wheel.visualOnlyUpdate = true;
                if (!wheel.UsingExternalUpdate) wheel.UseExternalUpdate();
                float previousSpring = wheel.spring != null ? wheel.spring.length : 0f;
                // Keep native HitUpdate/VisualUpdate: grounded state, surface hits, suspension,
                // steering and wheel spin are still needed by dust and ordinary tire sounds.
                wheel.OnPrePhysicsSubstep(Time.time, dt);
                RemoteWheelSuspension.PresentUngrounded(wheel, dt, previousSpring);
                presented = true;
                bool grounded = wheel.isGrounded;
                wheel.forwardFriction.speed = grounded ? Vector3.Dot(velocity, wheel.wheelHit.forwardDir) : 0f;
                wheel.sideFriction.speed = grounded ? Vector3.Dot(velocity, wheel.wheelHit.sidewaysDir) : 0f;
                if (wheel.wheel != null)
                {
                    wheel.wheel.load = grounded && vehicle.vehicleRigidbody != null && count > 0
                        ? vehicle.vehicleRigidbody.mass * 9.81f / count : 0f;
                    if (snapped) wheel.wheel.angularVelocity = 0f;
                    wheel.wheel.prevAngularVelocity = wheel.wheel.angularVelocity;
                    component.angularVelocity = snapped ? 0f : wheel.wheel.angularVelocity;
                }
                if (wheel.damper != null && wheel.spring != null)
                    wheel.damper.force = grounded && !snapped ? (previousSpring - wheel.spring.length) / dt *
                        Mathf.Max(wheel.damper.bumpForce, wheel.damper.reboundForce) : 0f;
                // The bus pose schema carries no host tire-slip samples. Neither movement nor
                // braking alone proves a skid; leave slip neutral instead of synthesizing one.
                wheel.forwardFriction.slip = wheel.sideFriction.slip = 0f;
                wheel.forwardFriction.force = wheel.sideFriction.force = 0f;
            }
            return presented;
        }

        internal static void Restore(VehicleController vehicle, bool awake, bool enabled)
        {
            if (vehicle.IsAwake != awake)
            {
                if (awake) vehicle.Wake();
                else vehicle.Sleep();
            }
            if (vehicle.enabled != enabled) vehicle.enabled = enabled;
        }
    }
}
