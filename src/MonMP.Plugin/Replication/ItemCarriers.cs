using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Shared vehicles and wheelbarrows that loose items can ride in. Each carrier's collider volume is
    // measured once in its own space; an item inside it is shared relative to the carrier (#119/#120/#127).
    internal sealed class ItemCarriers
    {
        private sealed class Carrier
        {
            public ulong Id; public Transform Frame = null!; public Bounds Local; public float Radius;
            public Collider[] Colliders = Array.Empty<Collider>(); public float MeasuredAt = float.NegativeInfinity;
            public int PreparedAt = -1;
            public readonly List<(Rigidbody Body, bool Walker)> Riders = new List<(Rigidbody, bool)>();
        }
        private const float Headroom = 0.6f;
        private readonly Dictionary<ulong, Carrier> carriers = new Dictionary<ulong, Carrier>();
        private readonly List<Func<IEnumerable<(ulong Id, Transform Frame)>>> providers = new List<Func<IEnumerable<(ulong, Transform)>>>();
        private float refreshedAt = float.NegativeInfinity;
        private const int MaxOverlapCapacity = 4096;
        private Collider[] overlap = new Collider[64];
        private bool warnedOverlapFallback;
        private readonly HashSet<ulong> seen = new HashSet<ulong>();
        private readonly List<ulong> removed = new List<ulong>();
        private readonly HashSet<Rigidbody> moved = new HashSet<Rigidbody>();

        public void Add(Func<IEnumerable<(ulong Id, Transform Frame)>> provider) => providers.Add(provider);
        internal Func<Transform, Transform?>? DetachedSupportFrame { get; set; }
        internal bool IsSupport(Transform surface, Transform frame) => surface.IsChildOf(frame) || DetachedSupportFrame?.Invoke(surface) == frame;

        private Collider[] RiderOverlaps(Vector3 center, Vector3 half, Quaternion rotation, out int count)
        {
            while (true)
            {
                count = Physics.OverlapBoxNonAlloc(center, half, overlap, rotation, ~0, QueryTriggerInteraction.Ignore);
                if (count < overlap.Length) return overlap;
                // A full NonAlloc result is an arbitrary subset, even when the true count
                // equals the capacity. Retry before choosing any riders from that subset.
                Array.Clear(overlap, 0, count);
                if (overlap.Length < MaxOverlapCapacity)
                {
                    overlap = new Collider[Math.Min(overlap.Length * 2, MaxOverlapCapacity)];
                    continue;
                }
                // Bound retained query memory. Exceptional dense volumes use a complete
                // transient result so a snap cannot silently move only some of the load.
                var complete = Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Ignore);
                count = complete.Length;
                if (!warnedOverlapFallback)
                {
                    warnedOverlapFallback = true;
                    Debug.LogWarning("MonMP carrier overlap filled the 4096-collider buffer; using complete allocating queries for saturated volumes.");
                }
                return complete;
            }
        }

        private void Refresh()
        {
            if (Time.unscaledTime - refreshedAt < 1f) return;
            refreshedAt = Time.unscaledTime;
            seen.Clear();
            foreach (var provider in providers)
                foreach (var (id, frame) in provider())
                {
                    if (frame == null || !frame.gameObject.activeInHierarchy) continue;
                    seen.Add(id);
                    if (!carriers.TryGetValue(id, out var carrier) || carrier.Frame != frame)
                        carriers[id] = new Carrier { Id = id, Frame = frame };
                }
            removed.Clear();
            foreach (var id in carriers.Keys) if (!seen.Contains(id)) removed.Add(id);
            foreach (var id in removed) carriers.Remove(id);
            removed.Clear(); seen.Clear();
        }

        private static void Measure(Carrier c)
        {
            // Parts come and go (a truck bed installed, a door removed): re-measure now and then.
            if (Time.unscaledTime - c.MeasuredAt < 30f) return;
            c.MeasuredAt = Time.unscaledTime;
            var colliders = c.Frame.GetComponentsInChildren<Collider>(false);
            var solid = new List<Collider>(colliders.Length);
            bool any = false; var local = new Bounds();
            foreach (var collider in colliders)
            {
                if (collider == null || collider.isTrigger) continue;
                solid.Add(collider);
                var b = collider.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = c.Frame.InverseTransformPoint(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; } else local.Encapsulate(p);
                }
            }
            if (!any) { c.Radius = 0; c.Colliders = Array.Empty<Collider>(); return; }
            // World AABBs of rotated colliders overestimate a little; that only widens the cargo volume.
            local.max += new Vector3(0, Headroom, 0);
            c.Local = local; c.Radius = local.extents.magnitude; c.Colliders = solid.ToArray();
        }

        // An item lying on the ground beside a car is inside its box, which reaches down past the wheels:
        // shared relative to the car, it was dragged along with a guest's buggy that touched it. Cargo
        // must rest on the carrier: the first solid thing under it, past other loose items, is the carrier.
        private static RaycastHit[] below = new RaycastHit[16];
        private static int SupportHits(Vector3 origin, float distance, ref RaycastHit[] buffer)
        {
            int count;
            do
            {
                count = Physics.RaycastNonAlloc(origin, Vector3.down, buffer, distance, ~0, QueryTriggerInteraction.Ignore);
                if (count < buffer.Length || buffer.Length >= 4096) return count;
                // NonAlloc hits are unsorted. A full buffer may contain only loose item
                // colliders while omitting the actual deck (or the nearer world floor).
                Array.Resize(ref buffer, buffer.Length * 2);
            } while (true);
        }
        private bool RestsOn(Vector3 world, Transform frame, Transform? self)
        {
            int hits = SupportHits(world + Vector3.up * 0.15f, 1.6f, ref below);
            float best = float.MaxValue; Transform? ground = null;
            for (int i = 0; i < hits; i++)
            {
                var t = below[i].collider.transform;
                if (self != null && t.IsChildOf(self)) continue;
                var body = below[i].collider.attachedRigidbody;
                if (body != null && !body.isKinematic && !t.IsChildOf(frame)) continue;
                if (t.CompareTag("Player")) continue;
                if (below[i].distance < best) { best = below[i].distance; ground = t; }
            }
            return ground != null && IsSupport(ground, frame);
        }

        public bool TryFind(Vector3 world, ulong exclude, out ulong id, out Transform frame, Transform? self = null)
        {
            Refresh();
            foreach (var c in carriers.Values)
            {
                if (c.Id == exclude || c.Frame == null) continue;
                Measure(c);
                if (c.Radius <= 0) continue;
                var center = c.Frame.TransformPoint(c.Local.center);
                if ((world - center).sqrMagnitude > c.Radius * c.Radius) continue;
                if (!c.Local.Contains(c.Frame.InverseTransformPoint(world))) continue;
                if (self != null)
                {
                    // Native cargo's render Transform is interpolated; support rays see
                    // current physics colliders. Query from the same physics step.
                    var body = self.GetComponent<Rigidbody>();
                    if (!RestsOn(body != null ? body.position : world, c.Frame, self)) continue;
                }
                id = c.Id; frame = c.Frame; return true;
            }
            id = 0; frame = null!; return false;
        }

        // The carrier a walker stands on: the nearest solid thing under the feet belongs to it.
        private static RaycastHit[] feetHits = new RaycastHit[8];
        public bool StandingOn(Vector3 feet, Transform walker, out ulong id, out Transform frame)
        {
            id = 0; frame = null!;
            Refresh();
            if (carriers.Count == 0) return false;
            int hits = SupportHits(feet + Vector3.up * 0.5f, 1.1f, ref feetHits);
            float best = float.MaxValue; Transform? ground = null;
            for (int i = 0; i < hits; i++)
            {
                var t = feetHits[i].collider.transform;
                if (walker != null && t.IsChildOf(walker)) continue;
                if (t.CompareTag("Player")) continue;
                if (feetHits[i].distance < best) { best = feetHits[i].distance; ground = t; }
            }
            if (ground == null) return false;
            foreach (var c in carriers.Values)
                if (c.Frame != null && IsSupport(ground, c.Frame)) { id = c.Id; frame = c.Frame; return true; }
            return false;
        }

        public bool IdOf(Transform frame, out ulong id)
        {
            Refresh();
            foreach (var c in carriers.Values) if (c.Frame == frame) { id = c.Id; return true; }
            id = 0; return false;
        }

        // Native vehicle containers keep their cargo parent even after restoring its Rigidbody.
        // A frozen load is attached outright; thawed cargo must still rest on that carrier so an
        // item which later falls onto the road does not follow its old parent forever.
        public bool TryFindAttached(Transform item, bool requireSupport, out ulong id, out Transform frame)
        {
            Refresh();
            var body = requireSupport ? item.GetComponent<Rigidbody>() : null;
            for (var parent = item.parent; parent != null; parent = parent.parent)
                foreach (var c in carriers.Values)
                {
                    if (c.Frame == null || c.Frame != parent) continue;
                    if (requireSupport && !RestsOn(body != null ? body.position : item.position, c.Frame, item))
                    { id = 0; frame = null!; return false; }
                    id = c.Id; frame = c.Frame; return true;
                }
            id = 0; frame = null!; return false;
        }

        public Transform? Resolve(ulong id)
        {
            Refresh();
            return carriers.TryGetValue(id, out var c) && c.Frame != null ? c.Frame : null;
        }

        internal IEnumerable<(ulong Id, Transform Frame)> Frames()
        {
            Refresh();
            foreach (var carrier in carriers.Values)
                if (carrier.Frame != null && carrier.Frame.gameObject.activeInHierarchy) yield return (carrier.Id, carrier.Frame);
        }

        internal bool OwnsSpace(ulong id, Transform space)
        {
            // A hit on an attached trailer belongs to its nearest registered frame,
            // never to an enclosing tow vehicle hierarchy.
            for (var parent = space; parent != null; parent = parent.parent)
                foreach (var carrier in carriers.Values)
                    if (carrier.Frame == parent) return carrier.Id == id;
            return false;
        }

        internal static void CapturePose(Transform item, Rigidbody? body, Transform frame, out Vector3 localPosition, out Quaternion localRotation)
        {
            var carrierBody = body != null ? frame.GetComponent<Rigidbody>() : null;
            if (body == null || carrierBody == null)
            {
                // Bodyless native frozen loads already belong to the parent's render pose.
                localPosition = frame.InverseTransformPoint(item.position);
                localRotation = Quaternion.Inverse(frame.rotation) * item.rotation;
                return;
            }
            var inverse = Quaternion.Inverse(carrierBody.rotation);
            var position = inverse * (body.position - carrierBody.position);
            var scale = frame.lossyScale;
            localPosition = new Vector3(position.x / scale.x, position.y / scale.y, position.z / scale.z);
            localRotation = inverse * body.rotation;
        }

        public Collider[] Colliders(ulong id)
        {
            if (!carriers.TryGetValue(id, out var c) || c.Frame == null) return Array.Empty<Collider>();
            Measure(c); return c.Colliders;
        }

        // A remote-driven carrier is moved by presentation, not physics: a kinematic body teleported each
        // frame leaves resting cargo and a standing player behind, which then rattle against the bed.
        // Move whatever dynamic body rides in it (and the local walker) by the same step.
        // walkerOnly: the carrier moved through physics, which already carries resting bodies by friction.
        private static RaycastHit[] under = new RaycastHit[8];
        private bool StandsOn(Vector3 feet, Transform frame)
        {
            // The frame has already moved this step; probe where the walker's feet were relative to it.
            int hits = SupportHits(feet + Vector3.up * 0.5f, 2.2f, ref under);
            float best = float.MaxValue; Transform? ground = null;
            for (int i = 0; i < hits; i++)
            {
                var t = under[i].collider.transform;
                if (under[i].collider.attachedRigidbody != null && !under[i].collider.attachedRigidbody.isKinematic && !t.IsChildOf(frame)) continue;
                if (t.CompareTag("Player")) continue;
                if (under[i].distance < best) { best = under[i].distance; ground = t; }
            }
            return ground != null && IsSupport(ground, frame);
        }

        private static Transform? viewWalker;
        private static CMF.CameraController? view;
        private static void TurnView(Transform walker, Quaternion from, Quaternion to)
        {
            var before = Vector3.ProjectOnPlane(from * Vector3.forward, Vector3.up);
            var after = Vector3.ProjectOnPlane(to * Vector3.forward, Vector3.up);
            if (before.sqrMagnitude < 1e-4f || after.sqrMagnitude < 1e-4f) return;
            float yaw = Vector3.SignedAngle(before, after, Vector3.up);
            if (Mathf.Abs(yaw) < 0.001f || Mathf.Abs(yaw) > 45f) return;
            if (viewWalker != walker) { viewWalker = walker; view = walker.GetComponent<CMF.AdvancedWalkerController>()?.playerCameraController; }
            if (view == null) return;
            view.SetRotationAngles(view.GetCurrentXAngle(), view.GetCurrentYAngle() + yaw);
        }

        // Query support while the carrier and the physics collider still agree. Presentation then
        // teleports the carrier in Update; probing the projected destination before the next physics
        // sync can miss its old broadphase bounds and leave a real rider behind at road speed.
        public void PrepareRiders(ulong id, Transform walker, bool walkerOnly = false)
        {
            Refresh();
            if (!carriers.TryGetValue(id, out var c) || c.Frame == null) return;
            c.PreparedAt = Time.frameCount; c.Riders.Clear();
            Measure(c);
            if (c.Radius <= 0) return;
            var frame = c.Frame;
            var center = frame.TransformPoint(c.Local.center);
            var half = Vector3.Scale(c.Local.extents, frame.lossyScale);
            var overlaps = RiderOverlaps(center, half, frame.rotation, out int count);
            moved.Clear();
            try
            {
                // A roof rider's capsule may sit above the measured cargo headroom. Its direct
                // support query is sufficient; discovering a walker must not depend on cargo overlap.
                var walkingBody = walker != null ? walker.GetComponent<Rigidbody>() : null;
                if (walkingBody != null && walker != null && !walkingBody.isKinematic && !walker.IsChildOf(frame) && StandsOn(walkingBody.position, frame))
                { c.Riders.Add((walkingBody, true)); moved.Add(walkingBody); }
                for (int i = 0; i < count; i++)
                {
                    var body = overlaps[i].attachedRigidbody;
                    if (body == null || body.isKinematic || !moved.Add(body) || body.transform.IsChildOf(frame)) continue;
                    bool isWalker = walker != null && body.transform == walker;
                    if (walkerOnly && !isWalker) continue;
                    if (isWalker ? !StandsOn(body.position, frame) : !RestsOn(body.position, frame, body.transform)) continue;
                    c.Riders.Add((body, isWalker));
                }
            }
            finally { Array.Clear(overlaps, 0, count); moved.Clear(); }
        }

        public void CarryRiders(ulong id, Vector3 fromPosition, Quaternion fromRotation, Transform walker, bool walkerOnly = false)
        {
            if (walkerOnly && walker == null) return;
            if (!carriers.TryGetValue(id, out var c) || c.Frame == null) return;
            var frame = c.Frame;
            var delta = frame.rotation * Quaternion.Inverse(fromRotation);
            if ((frame.position - fromPosition).sqrMagnitude < 1e-8f && Quaternion.Angle(delta, Quaternion.identity) < 0.01f)
            { c.Riders.Clear(); c.PreparedAt = -1; return; }
            if (c.PreparedAt == Time.frameCount)
            {
                foreach (var rider in c.Riders)
                {
                    var body = rider.Body;
                    if (body == null || body.isKinematic || (walkerOnly && !rider.Walker)) continue;
                    var position = frame.position + delta * (body.position - fromPosition);
                    body.position = position; body.transform.position = position;
                    if (!rider.Walker)
                    { var rotation = delta * body.rotation; body.rotation = rotation; body.transform.rotation = rotation; }
                    else TurnView(body.transform, fromRotation, frame.rotation);
                }
                c.Riders.Clear(); c.PreparedAt = -1;
                return;
            }
            c.Riders.Clear(); c.PreparedAt = -1;
            // A parked carrier cannot carry riders this step. Avoid periodic hierarchy measurement
            // until movement or an item lookup actually needs its cargo volume.
            Measure(c);
            if (c.Radius <= 0) return;
            // The carrier's volume where it was, before this step moved it.
            var center = fromPosition + fromRotation * Vector3.Scale(c.Local.center, frame.lossyScale);
            var half = Vector3.Scale(c.Local.extents, frame.lossyScale);
            var overlaps = RiderOverlaps(center, half, fromRotation, out int count);
            moved.Clear();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var body = overlaps[i].attachedRigidbody;
                    if (body == null || body.isKinematic || !moved.Add(body) || body.transform.IsChildOf(frame)) continue;
                    bool isWalker = walker != null && body.transform == walker;
                    if (walkerOnly && !isWalker) continue;
                    // The volume is the carrier's whole box: a player beside a passing car overlapped it and was
                    // dragged along (#198). A walker rides only while standing on the carrier itself.
                    var position = frame.position + delta * (body.position - fromPosition);
                    if (isWalker && !StandsOn(position, frame)) continue;
                    // Likewise an item beside it on the ground (the box reaches down past the wheels).
                    if (!isWalker && !RestsOn(position, frame, body.transform)) continue;
                    body.position = position; body.transform.position = position;
                    // Items turn with the carrier; the walker stays upright (CMF gravity follows its up axis),
                    // but its view turns with the carrier's yaw, as a seated player's does (#249).
                    if (!isWalker) { var rotation = delta * body.rotation; body.rotation = rotation; body.transform.rotation = rotation; }
                    else TurnView(body.transform, fromRotation, frame.rotation);
                }
            }
            finally { Array.Clear(overlaps, 0, count); moved.Clear(); }
        }
    }
}
