using System;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Only the detached spectator camera uses this filter. Native vehicle/player physics
    // remain unchanged, and the visible remote head has no colliders of its own.
    internal sealed class SpectatorCameraCollision
    {
        private RaycastHit[] hits = new RaycastHit[32];

        internal float CameraDistance(Vector3 focus, Vector3 direction, float wanted)
        {
            int count;
            while (true)
            {
                count = Physics.SphereCastNonAlloc(focus, 0.16f, direction, hits, wanted,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (count < hits.Length) break;
                // NonAlloc results are unordered and may omit a wall behind a dense vehicle.
                // Retry with a larger retained buffer before choosing the nearest solid hit.
                Array.Resize(ref hits, checked(hits.Length * 2));
            }

            float nearest = wanted;
            bool blocked = false;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || Ignores(hit.collider)) continue;
                if (hit.distance <= nearest)
                {
                    nearest = hit.distance;
                    blocked = true;
                }
            }
            return blocked ? Mathf.Max(0.2f, nearest - 0.18f) : wanted;
        }

        internal static bool Ignores(Collider collider) =>
            // Vehicle covers cars, trailers and boats. Parts/seats are also checked
            // independently so detached parts and unparented seat interactables pass through.
            collider.GetComponentInParent<Vehicle>(true) != null ||
            collider.GetComponentInParent<Vehicle_PartsManager>(true) != null ||
            collider.GetComponentInParent<InteractableParts>(true) != null ||
            collider.GetComponentInParent<Generated_Part>(true) != null ||
            collider.GetComponentInParent<Vehicle_DriverSeat>(true) != null ||
            collider.GetComponentInParent<Vehicle_PassengerSeat>(true) != null ||
            collider.GetComponentInParent<GleyTrafficSystem.VehicleComponent>(true) != null;
    }
}
