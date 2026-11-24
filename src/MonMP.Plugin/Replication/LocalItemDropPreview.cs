using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Brief guest presentation only. The authoritative lease, body settings and velocity
    // remain untouched while a plain drop travels to the host.
    internal sealed class LocalItemDropPreview
    {
        internal const float Duration = 1f;
        private const float MaxStep = 0.05f, Skin = 0.002f;
        private const int QueryCapacity = 64;
        private readonly InteractableItems source;
        private readonly Rigidbody body;
        private readonly List<Collider> shapes = new List<Collider>();
        private readonly Collider[] overlaps = new Collider[QueryCapacity];
        private readonly RaycastHit[] hits = new RaycastHit[QueryCapacity];
        private readonly Vector3 gravity, initialVelocity;
        private readonly float deadline;
        private Vector3 position;
        private float lastNow, elapsed;
        private bool invalidated;

        private LocalItemDropPreview(InteractableItems item, Rigidbody body, uint generation, float now, Vector3 gravity, Vector3 velocity)
        {
            source = item; this.body = body; Generation = generation; this.gravity = gravity; initialVelocity = velocity;
            StartPosition = position = item.transform.position; lastNow = now; deadline = now + Duration;
        }

        internal uint Generation { get; }
        internal Vector3 StartPosition { get; }
        internal bool Active { get; private set; }
        internal bool StoppedOnContact { get; private set; }
        // An accepted release can continue this same trajectory through its handoff.
        internal bool HasMotion { get; private set; }
        internal bool CanRetainPose => HasMotion && !invalidated;
        internal float Speed => Active ? (initialVelocity + gravity * elapsed).magnitude : 0;

        internal static LocalItemDropPreview? Begin(InteractableItems item, Transform localPlayer, uint generation, float now, Vector3 velocity = default)
        {
            if (item == null || localPlayer == null || !Finite(now) || generation == 0 || item.rb == null ||
                !item.rb.isKinematic || !item.rb.useGravity || !item.HasARigidBody ||
                !item.gameObject.activeInHierarchy || item.transform.parent != null || !Finite(item.transform.position) ||
                !Finite(velocity) || velocity.sqrMagnitude > 16.01f ||
                !Finite(Physics.gravity) || Physics.gravity.sqrMagnitude < 1e-8f) return null;
            var preview = new LocalItemDropPreview(item, item.rb, generation, now, Physics.gravity, velocity);
            item.GetComponentsInChildren(false, preview.shapes);
            if (item.itemCollider == null || !item.itemCollider.enabled || !item.itemCollider.gameObject.activeInHierarchy ||
                !preview.shapes.Contains(item.itemCollider)) return null;
            if (!preview.TryBounds(out var bounds) || preview.Blocked(bounds, localPlayer)) return null;
            preview.Active = true;
            return preview;
        }

        // Returns whether this call translated the copy. No movement is replayed after a
        // stalled frame: the real-time deadline bounds the preview even when scaled time stops.
        internal bool Step(InteractableItems item, Transform localPlayer, float now, float deltaTime)
        {
            if (item == null || item != source || localPlayer == null || body == null || item.rb != body || !body.isKinematic ||
                !body.useGravity || !item.HasARigidBody || !item.gameObject.activeInHierarchy || item.transform.parent != null ||
                !Finite(now) || !Finite(deltaTime) || deltaTime < 0 || now < lastNow ||
                !Finite(item.transform.position) || (item.transform.position - position).sqrMagnitude > 1e-8f)
                return Invalidate();
            lastNow = now;
            // Inactive previews still reject a changed body/pose, without doing any
            // collision query. A pending or acknowledged drop follows one uninterrupted
            // trajectory until contact or its real-time handoff deadline.
            if (!Active) return false;
            if (now >= deadline) { Active = false; return false; }
            float dt = Math.Min(Math.Min(deltaTime, MaxStep), Duration - elapsed);
            if (dt <= 0) return false;
            if (!TryBounds(out var bounds) || Blocked(bounds, localPlayer))
                return Invalidate();
            float next = elapsed + dt;
            var displacement = initialVelocity * dt + gravity * (0.5f * (next * next - elapsed * elapsed));
            float distance = displacement.magnitude;
            if (!Finite(displacement) || !Finite(distance) || distance <= 0)
                return Invalidate();
            float allowed = distance;
            bool contact = false;
            if (!Sweep(displacement / distance, distance, localPlayer, ref allowed, ref contact))
            {
                return Invalidate();
            }
            elapsed = next;
            StoppedOnContact = contact;
            if (contact || elapsed >= Duration) Active = false;
            if (allowed <= 0) return false;
            var nextPosition = position + displacement * (allowed / distance);
            if (!Finite(nextPosition)) return Invalidate();
            position = nextPosition;
            body.position = position;
            item.transform.position = position;
            HasMotion = true;
            return true;
        }

        private bool Invalidate() { Active = false; invalidated = true; return false; }

        private bool Blocked(Bounds bounds, Transform localPlayer)
        {
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, overlaps, Quaternion.identity,
                ~0, QueryTriggerInteraction.Ignore);
            if (count >= overlaps.Length) return true;
            for (int i = 0; i < count; i++)
            {
                var obstacle = overlaps[i];
                if (Ignored(obstacle, localPlayer)) continue;
                foreach (var shape in shapes)
                    if (Included(shape) && !PairIgnored(shape, obstacle) &&
                        Physics.ComputePenetration(shape, shape.transform.position, shape.transform.rotation,
                            obstacle, obstacle.transform.position, obstacle.transform.rotation, out _, out _)) return true;
            }
            return false;
        }

        private bool Sweep(Vector3 direction, float distance, Transform localPlayer, ref float allowed, ref bool contact)
        {
            bool mesh = false;
            foreach (var shape in shapes) if (Included(shape) && shape is MeshCollider) { mesh = true; break; }
            if (mesh)
            {
                // Unity has no NonAlloc convex-mesh sweep. Use its native shape sweep only
                // for this short active preview; it also preserves thin obstacle detection.
                var bodyHits = body.SweepTestAll(direction, distance + Skin, QueryTriggerInteraction.Ignore);
                return bodyHits.Length < 128 && CheckHits(bodyHits, bodyHits.Length, null, localPlayer, ref allowed, ref contact);
            }
            foreach (var shape in shapes)
            {
                if (!Included(shape)) continue;
                var t = shape.transform;
                var scale = t.lossyScale;
                scale = new Vector3(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
                int count;
                if (shape is BoxCollider box)
                    count = Physics.BoxCastNonAlloc(t.TransformPoint(box.center), Vector3.Scale(box.size * 0.5f, scale),
                        direction, hits, t.rotation, distance + Skin, ~0, QueryTriggerInteraction.Ignore);
                else if (shape is SphereCollider sphere)
                    count = Physics.SphereCastNonAlloc(t.TransformPoint(sphere.center), sphere.radius * Math.Max(scale.x, Math.Max(scale.y, scale.z)),
                        direction, hits, distance + Skin, ~0, QueryTriggerInteraction.Ignore);
                else if (shape is CapsuleCollider capsule)
                {
                    Vector3 axis; float lengthScale, radiusScale;
                    switch (capsule.direction)
                    {
                        case 0: axis = t.right; lengthScale = scale.x; radiusScale = Math.Max(scale.y, scale.z); break;
                        case 1: axis = t.up; lengthScale = scale.y; radiusScale = Math.Max(scale.x, scale.z); break;
                        case 2: axis = t.forward; lengthScale = scale.z; radiusScale = Math.Max(scale.x, scale.y); break;
                        default: return false;
                    }
                    float radius = capsule.radius * radiusScale;
                    var half = axis * Math.Max(0, capsule.height * lengthScale * 0.5f - radius);
                    var center = t.TransformPoint(capsule.center);
                    count = Physics.CapsuleCastNonAlloc(center - half, center + half, radius, direction, hits,
                        distance + Skin, ~0, QueryTriggerInteraction.Ignore);
                }
                else return false;
                if (count >= hits.Length || !CheckHits(hits, count, shape, localPlayer, ref allowed, ref contact)) return false;
            }
            return true;
        }

        private bool CheckHits(RaycastHit[] results, int count, Collider? sweptShape, Transform localPlayer, ref float allowed, ref bool contact)
        {
            for (int i = 0; i < count; i++)
            {
                var obstacle = results[i].collider;
                if (Ignored(obstacle, localPlayer)) continue;
                if (sweptShape != null)
                { if (PairIgnored(sweptShape, obstacle)) continue; }
                else
                {
                    bool physicalPair = false;
                    foreach (var shape in shapes)
                        if (Included(shape) && !PairIgnored(shape, obstacle)) { physicalPair = true; break; }
                    if (!physicalPair) continue;
                }
                if (!Finite(results[i].distance) || results[i].distance < 0) return false;
                allowed = Math.Min(allowed, Math.Max(0, results[i].distance - Skin));
                contact = true;
            }
            return true;
        }

        private bool Included(Collider shape) => shape != null && shape.enabled && shape.gameObject.activeInHierarchy &&
            (shape == source.itemCollider || !shape.isTrigger);
        private static bool PairIgnored(Collider shape, Collider obstacle) =>
            Physics.GetIgnoreLayerCollision(shape.gameObject.layer, obstacle.gameObject.layer) || Physics.GetIgnoreCollision(shape, obstacle);

        private bool Ignored(Collider collider, Transform localPlayer) => collider == null || !collider.enabled ||
            !collider.gameObject.activeInHierarchy || collider.isTrigger ||
            collider.transform.IsChildOf(source.transform) || collider.transform.IsChildOf(localPlayer);

        private bool TryBounds(out Bounds result)
        {
            result = default;
            if (source.itemCollider == null || !source.itemCollider.enabled || !source.itemCollider.gameObject.activeInHierarchy ||
                !shapes.Contains(source.itemCollider)) return false;
            bool found = false;
            foreach (var shape in shapes)
            {
                if (shape == null || !shape.enabled || !shape.gameObject.activeInHierarchy) continue;
                if (shape != source.itemCollider && shape.isTrigger) continue;
                // Moving a nested independent body, or guessing an unsupported shape, could
                // miss the actual solid envelope. Leave those drops to the host.
                if (!shape.transform.IsChildOf(source.transform) || shape.attachedRigidbody != body ||
                    !(shape is BoxCollider || shape is SphereCollider || shape is CapsuleCollider ||
                      shape is MeshCollider mesh && mesh.convex && mesh.sharedMesh != null)) return false;
                var bounds = shape.bounds;
                var extents = bounds.extents;
                if (!Finite(bounds.center) || !Finite(extents) || extents.x <= 0 || extents.y <= 0 || extents.z <= 0 ||
                    extents.x > 32 || extents.y > 32 || extents.z > 32 ||
                    (bounds.center - source.transform.position).sqrMagnitude > 4096) return false;
                if (!found) { result = bounds; found = true; }
                else result.Encapsulate(bounds);
            }
            return found;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
