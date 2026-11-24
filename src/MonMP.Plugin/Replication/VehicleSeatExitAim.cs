using NWH;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class VehicleSeatExitAim
    {
        // Native interaction takes only the first collider hit. From inside a
        // saddle/cushion collider that can miss the occupied seat altogether.
        // Only the caller's occupied native seat is eligible for this fallback.
        internal static Interactable? Pick(Interactable seat, Transform position, Vehicle vehicle, Ray ray)
        {
            if (seat == null || !seat.isActiveAndEnabled || position == null || !position.gameObject.activeInHierarchy ||
                !VehicleSeatDoors.AllowsExit(vehicle) || ContainerHoleAim.Target(ray)) return null;
            var cushion = seat.meshRenderer != null ? seat.meshRenderer.bounds.center : position.position;
            float down = Vector3.Dot(ray.direction, position.up);
            if (down > -0.25f) return null;
            float distance = Vector3.Dot(cushion - ray.origin, position.up) / down;
            if (distance < 0.1f || distance > 2.4f ||
                Vector3.ProjectOnPlane(ray.GetPoint(distance) - cushion, position.up).sqrMagnitude > 0.45f * 0.45f) return null;
            if (Physics.Raycast(ray, out var hit, 2.4f, ~IgnoreLayers))
            {
                if (hit.collider.CompareTag("ContainerHole") ||
                    hit.collider.gameObject.layer == LayerMask.NameToLayer("ContainerHole")) return null;
                var other = hit.collider.GetComponentInParent<Interactable>();
                // A control, door handle, loose item or anything outside this
                // chassis keeps its normal pick. Its own seat surface/body may
                // obscure the native seat button, just as on the entry picker.
                if (other != null && other != seat &&
                    !(other is Part_Seat part && part.isPlaced && part.transform.IsChildOf(seat.transform.parent))) return null;
                if (!hit.collider.transform.IsChildOf(vehicle.transform) && hit.distance < distance) return null;
            }
            return seat;
        }

        private static int ignoreLayers = -1;
        private static int IgnoreLayers
        {
            get
            {
                if (ignoreLayers != -1) return ignoreLayers;
                int value = 0;
                foreach (var name in new[] { "Player", "PartSocket", "Ignore Raycast", "MainVehicleTrigger", "SwimmingPlayerSurface", "IgnoreByPlayerInteractable" })
                { int layer = LayerMask.NameToLayer(name); if (layer >= 0) value |= 1 << layer; }
                return ignoreLayers = value;
            }
        }
    }
}
