using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class LoosePartBody
    {
        internal static void Repair(InteractableParts part, bool locallyHeld)
        {
            // Installation and the tire changer deliberately disable these colliders.
            // Only a detached, unheld loose part can regain the native pickup contract.
            if (part.isPlaced || locallyHeld || part.transform.parent != null || !part.gameObject.activeInHierarchy) return;
            part.TempPlaced = false;
            if (part.rb == null) part.CreateBasics();
            // Native CreateBasics only rebinds the collider when it creates/rebinds a
            // body. Keep an existing body's collider and its initialized material.
            if (part.itemCollider == null) part.itemCollider = part.GetComponent<Collider>();
            int layer = LayerMask.NameToLayer("InteractableItem");
            if (part.gameObject.layer != layer) part.gameObject.layer = layer;
            if (part.itemCollider != null && !part.itemCollider.enabled) part.itemCollider.enabled = true;
        }
    }
}
