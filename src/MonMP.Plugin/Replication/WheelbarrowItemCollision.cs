using System;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Guest items follow host physics and cannot be pushed by the transform-driven tray.
    // Sweep its real solid shapes against loose replicas before publishing/keeping motion.
    // No replica body, collision pair, item lease or cargo transform is changed here.
    internal sealed class WheelbarrowItemCollision
    {
        private readonly Transform root;
        private readonly Collider[] shapes;
        private readonly Collider[] nearby = new Collider[256];
        private Vector3 previous;
        private Quaternion rotation;
#if MONMP_DEVELOPMENT
        internal int TestShapeCount => shapes.Length;
#endif

        internal WheelbarrowItemCollision(Transform root)
        {
            this.root = root;
            shapes = root.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger &&
                c.GetComponentInParent<InteractableItems>() == null).ToArray();
            previous = root.position; rotation = root.rotation;
        }

        internal bool Constrain(Transform player)
        {
            if (root == null || player == null || shapes.Length == 0) return false;
            var desired = root.position; var desiredRotation = root.rotation;
            float distance = Vector3.Distance(previous, desired), angle = Quaternion.Angle(rotation, desiredRotation);
            if (distance < 0.0001f && angle < 0.01f) return false;
            // Scene placement/teleports are not a walk through an obstacle.
            if (distance > 3f) { previous = desired; rotation = desiredRotation; return false; }
            Bounds sweep = default; bool found = false;
            foreach (var shape in shapes)
            {
                if (!Solid(shape)) continue;
                if (!found) { sweep = shape.bounds; found = true; } else sweep.Encapsulate(shape.bounds);
            }
            if (!found) { previous = desired; rotation = desiredRotation; return false; }
            sweep.Encapsulate(new Bounds(sweep.center + previous - desired, sweep.size));
            // Rotation can take a corner beyond the two endpoint boxes.
            sweep.Expand(Vector3.one * (0.02f + Mathf.Sin(angle * Mathf.Deg2Rad * 0.5f) * sweep.size.magnitude));
            int count = Physics.OverlapBoxNonAlloc(sweep.center, sweep.extents, nearby, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(distance / 0.025f, angle / 3f)), 1, 128);
            float safe = 0;
            bool blocked = count == nearby.Length;
            for (int step = 1; step <= steps && !blocked; step++)
            {
                float at = (float)step / steps;
                if (Blocks(at, desired, desiredRotation, count))
                {
                    // Refine to contact rather than leaving a frame-sized gap. A tiny contact
                    // allowance lets the host's dynamic prop respond to its replicated tray.
                    float upper = at;
                    for (int refine = 0; refine < 8; refine++)
                    {
                        float middle = (safe + upper) * 0.5f;
                        if (Blocks(middle, desired, desiredRotation, count)) upper = middle;
                        else safe = middle;
                    }
                    blocked = true;
                }
                if (!blocked) safe = at;
            }
            Array.Clear(nearby, 0, count);
            var accepted = Vector3.Lerp(previous, desired, safe);
            var acceptedTurn = Quaternion.Slerp(rotation, desiredRotation, safe);
            if (blocked)
            {
                var corrected = player.position + accepted - desired;
                player.position = corrected;
                var body = player.GetComponent<Rigidbody>();
                if (body != null) body.position = corrected;
                root.SetPositionAndRotation(accepted, acceptedTurn);
            }
            previous = accepted; rotation = acceptedTurn;
            return blocked;
        }

        private bool Blocks(float at, Vector3 desired, Quaternion desiredRotation, int count)
        {
            var position = Vector3.Lerp(previous, desired, at);
            var turn = Quaternion.Slerp(rotation, desiredRotation, at);
            foreach (var shape in shapes)
            {
                if (!Solid(shape)) continue;
                var local = Vector3.Scale(root.InverseTransformPoint(shape.transform.position), root.lossyScale);
                var localTurn = Quaternion.Inverse(desiredRotation) * shape.transform.rotation;
                var shapeAt = position + turn * local;
                var shapeBefore = previous + rotation * local;
                for (int i = 0; i < count; i++)
                {
                    var obstacle = nearby[i];
                    if (!LooseItem(obstacle) || Physics.GetIgnoreLayerCollision(shape.gameObject.layer, obstacle.gameObject.layer) ||
                        Physics.GetIgnoreCollision(shape, obstacle)) continue;
                    float depth = Depth(shape, shapeAt, turn * localTurn, obstacle);
                    if (depth <= 0.002f) continue;
                    // Allow the player or the host's prop to separate an existing contact.
                    float before = Depth(shape, shapeBefore, rotation * localTurn, obstacle);
                    if (depth > before + 0.0001f) return true;
                }
            }
            return false;
        }

        private static bool Solid(Collider shape) => shape != null && shape.enabled && !shape.isTrigger && shape.gameObject.activeInHierarchy;
        private bool LooseItem(Collider shape)
        {
            if (!Solid(shape) || shape.transform.IsChildOf(root)) return false;
            var item = shape.GetComponentInParent<InteractableItems>();
            return item != null && item.HasARigidBody && item.rb != null && item.itemCollider != null && !item.itemCollider.isTrigger;
        }
        private static bool Convex(Collider shape) => shape is BoxCollider || shape is SphereCollider || shape is CapsuleCollider || shape is MeshCollider mesh && mesh.convex;
        private static float Depth(Collider own, Vector3 position, Quaternion turn, Collider obstacle)
        {
            if (Convex(own))
                return Physics.ComputePenetration(own, position, turn, obstacle, obstacle.transform.position, obstacle.transform.rotation,
                    out _, out float depth) ? depth : 0;
            if (Convex(obstacle))
                return Physics.ComputePenetration(obstacle, obstacle.transform.position, obstacle.transform.rotation, own, position, turn,
                    out _, out float depth) ? depth : 0;
            return 0; // PhysX does not support contacts between two concave mesh shapes.
        }
    }
}
