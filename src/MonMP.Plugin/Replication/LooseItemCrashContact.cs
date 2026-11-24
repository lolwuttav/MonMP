using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Loose objects still collide physically. They cannot initiate chassis damage,
    // crash audio or occupant crash effects, wherever they are resting or moving.
    // Classify the actual native body rather than a sampled cargo/lease record.
    internal static class LooseItemCrashContact
    {
        internal static bool IsLooseItem(Collision? collision)
        {
            var collider = collision?.collider;
            if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) return false;
            var item = collider.GetComponentInParent<InteractableItems>();
            var body = collision!.rigidbody;
            if (item == null || !item.gameObject.activeInHierarchy || !item.HasARigidBody || body == null ||
                body.transform != item.transform || collider.attachedRigidbody != body) return false;
            // Native trunk splitting adds Item_WoodLog onto an existing body. Its
            // cached item.rb is filled only by Start; the actual contact body is
            // already authoritative during that first physics step.
            // An installed/native preview part can briefly retain its old body
            // while Unity defers destruction. It remains vehicle/environment geometry.
            if (item is InteractableParts part && (part.isPlaced || part.TempPlaced) ||
                item is Hardware_Bolt bolt && bolt.isPlaced) return false;
            return true;
        }
    }
}
