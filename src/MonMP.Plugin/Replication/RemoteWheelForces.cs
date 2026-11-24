using HarmonyLib;
using NWH;
using NWH.VehiclePhysics2;
using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // NWH's visual-only flag guards Pre, not its friction/force and Post callbacks.
    // Keep native Pre subscribed for contact, suspension visuals and our sampled slip/spin;
    // only a live, registered remote wheel may skip the two native force bodies.
    internal static class RemoteWheelForces
    {
        private static readonly AccessTools.FieldRef<WheelController, Vector3> TotalForce =
            AccessTools.FieldRefAccess<WheelController, Vector3>("_totalForceThisUpdate");
        private static readonly AccessTools.FieldRef<WheelController, float> ChassisTorque =
            AccessTools.FieldRefAccess<WheelController, float>("_chassisTorque");
        private static readonly AccessTools.FieldRef<WheelController, bool> EnabledThisFrame =
            AccessTools.FieldRefAccess<WheelController, bool>("_hasBeenEnabledThisFrame");
        private static readonly AccessTools.FieldRef<WheelController, bool> HasHitBody =
            AccessTools.FieldRefAccess<WheelController, bool>("_hasHitARigidbody");

        internal static bool Suppress(WheelController wheel)
        {
            // The common local-wheel path does not need world/index lookups.
            if (wheel == null || !wheel.visualOnlyUpdate) return false;
            var world = MonMPPlugin.Instance?.World;
            return world != null && world.Vehicles.SuppressesWheelForces(wheel);
        }

        internal static bool NativeReplica(WheelController wheel, VehicleController vehicle) =>
            wheel != null && wheel.visualOnlyUpdate && vehicle != null && vehicle.enabled && vehicle.gameObject.activeInHierarchy &&
            vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote &&
            vehicle.vehicleRigidbody != null && vehicle.vehicleRigidbody.isKinematic && wheel.parentRigidbody == vehicle.vehicleRigidbody &&
            wheel.parentNRigidbody != null && wheel.parentNRigidbody.targetRigidbody == vehicle.vehicleRigidbody && !NeedsNativeReaction(wheel);

        // HitUpdate in native Pre owns these cached contact fields; neither Physics nor
        // Post changes them. Both force callbacks therefore make the same decision for
        // this contact step. Keep the complete native pair for any possible body reaction,
        // including kinematic contacts, so host-owned loose items retain wheel impulses.
        private static bool NeedsNativeReaction(WheelController wheel) => wheel.applyForceToOthers && wheel.hasHit &&
            (HasHitBody(wheel) || wheel.Get_hitRigidbody != null);

        internal static void PresentRemoteStep(WheelController wheel)
        {
            // Preserve the native non-force outputs from the driver's Pre presentation,
            // rather than letting the local friction solver replace that sampled state.
            if (wheel.wheel != null) wheel.wheel.RPM = wheel.wheel.angularVelocity * 9.55f;
            if (wheel.hasHit && wheel.wheelHit != null)
            {
                wheel.wheelHit.forwardSlip = wheel.forwardFriction.slip;
                wheel.wheelHit.sidewaysSlip = wheel.sideFriction.slip;
            }
        }

        internal static void FinishRemoteStep(WheelController wheel)
        {
            // Preserve the exact native Post tail, without its force/torque application.
            // No subscriptions or lifecycle flags persist into local ownership: the first
            // local step takes the complete native Physics/Post bodies again.
            TotalForce(wheel) = Vector3.zero;
            ChassisTorque(wheel) = 0f;
            EnabledThisFrame(wheel) = false;
            if (wheel.wheel != null) wheel.wheel.prevAngularVelocity = wheel.wheel.angularVelocity;
        }
    }
}
