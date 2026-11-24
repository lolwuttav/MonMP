using System;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class SprayPaintRaycast
    {
        private static RaycastHit[] buffer = new RaycastHit[16];

        // The remote player's canonical held can remains a trigger on the host.
        // Ignore only that exact tool; native paint triggers and other obstructions
        // still participate, and the nearest remaining hit wins.
        internal static bool TryHit(Vector3 origin, Vector3 direction, float distance, int mask,
            Transform? heldTool, out RaycastHit hit) => TryQuery(origin, direction, distance, mask, heldTool, false, out hit);

        internal static bool CanUseWithCan(Interactable? interaction) => interaction != null && interaction.isCanBeClickedWithToolInHand &&
            interaction is not Vehicle_DriverSeat && interaction is not Vehicle_PassengerSeat && interaction is not Konig_Seat_Button &&
            interaction is not Furniture_Chair && interaction is not Furniture_Decoration_Seat &&
            (interaction.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>() == null ||
                interaction is Vehicle_DoorHandle || interaction is Vehicle_ExteriorHoodRelease ||
                interaction is Vehicle_InteriorHoodRelease || interaction is Vehicle_Trunk_Button || interaction is Part_FuelDoor);

        internal static Interactable? PickInteraction(Vector3 origin, Vector3 direction, int mask, Transform heldTool)
        {
            if (!TryQuery(origin, direction, 2.4f, mask, heldTool, true, out var hit)) return null;
            var interaction = hit.collider.GetComponent<Interactable>();
            return CanUseWithCan(interaction) ? interaction : null;
        }

        private static bool TryQuery(Vector3 origin, Vector3 direction, float distance, int mask,
            Transform? heldTool, bool interactionRay, out RaycastHit hit)
        {
            RaycastHit[] hits;
            int count;
            // Native paint targets include triggers. Their eligibility must not
            // depend on an unrelated native query changing the global setting.
            // Keep ordinary can-equipped interaction picking on its native policy.
            var triggers = interactionRay ? QueryTriggerInteraction.UseGlobal : QueryTriggerInteraction.Collide;
            while (true)
            {
                count = Physics.RaycastNonAlloc(origin, direction, buffer, distance, mask, triggers);
                if (count < buffer.Length) { hits = buffer; break; }
                Array.Clear(buffer, 0, count);
                if (buffer.Length >= 4096)
                { hits = Physics.RaycastAll(origin, direction, distance, mask, triggers); count = hits.Length; break; }
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            hit = default;
            float nearest = float.PositiveInfinity;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var candidate = hits[i];
                    if (candidate.collider == null || heldTool != null && candidate.collider.transform.IsChildOf(heldTool) ||
                        (interactionRay ? BlockedInteractionTrigger(candidate.collider) : NonPaintTrigger(candidate.collider)) ||
                        candidate.distance >= nearest) continue;
                    hit = candidate; nearest = candidate.distance;
                }
                return nearest < float.PositiveInfinity;
            }
            finally { Array.Clear(hits, 0, count); }
        }

        private static bool BlockedInteractionTrigger(Collider collider) => collider.isTrigger &&
            collider.GetComponent<Interactable>() is Interactable interaction && !CanUseWithCan(interaction) &&
            collider.GetComponent<Vehicle_PaintablePanel>() == null && collider.GetComponent<Generated_Part>() == null;

        // Host-side sensor volumes need not carry an Interactable component.
        // They are not opaque surfaces. Preserve actual paint targets and item
        // geometry, including an item's child trigger, as well as every solid hit.
        private static bool NonPaintTrigger(Collider collider) => collider.isTrigger &&
            collider.GetComponentInParent<InteractableItems>() == null &&
            collider.GetComponent<Vehicle_PaintablePanel>() == null &&
            collider.GetComponent<Generated_Part>() == null;
    }
}
