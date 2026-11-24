using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Measure a real compartment at the deliberate drop, using a supported floor
    // and enclosing walls. Native vehicle detectors are too broad (the truck's
    // includes its hood/ground); whole-vehicle bounds are never an eligibility zone.
    internal sealed class VehicleCargoSpace
    {
        private readonly Transform frame;
        private readonly Collider? floor;
        private readonly Coordinates coordinates;
        // local: where a load must lie to freeze. catchment: where a falling drop stays tracked.
        private readonly Bounds local, catchment;
        private readonly List<Collider> itemShapes = new List<Collider>();
        private readonly List<Vector3> meshVertices = new List<Vector3>();
        private Mesh? cachedMesh;
        private static RaycastHit[] hits = new RaycastHit[16];
        private static readonly List<Part_Suspension_Hub_Stake> stakeSides = new List<Part_Suspension_Hub_Stake>();
        private static readonly Vector3[] horizontal = { Vector3.left, Vector3.right, Vector3.back, Vector3.forward };
        private const float CargoHeadroom = 0.5f;
        private VehicleCargoSpace(Transform frame, Collider? floor, Bounds local, Bounds catchment)
        { this.frame = frame; this.floor = floor; this.local = local; this.catchment = catchment; coordinates = new Coordinates(frame); }
        // Hand-defined compartments (CargoBoundsOverrides) have no measured floor collider.
        internal bool Valid => frame != null && frame.gameObject.activeInHierarchy && (floor == null ? defined :
            floor.enabled && floor.transform.gameObject.activeInHierarchy && floor.transform.IsChildOf(frame));
        private bool defined;
        internal bool Contains(Vector3 point) => Valid && catchment.Contains(coordinates.Local(point));
        // Frame-local boxes per carrier model; a model with boxes uses only those.
        internal static Func<Transform, Bounds[]?>? DefinedBounds { get; set; }
        private const float DefinedDropHeight = 1.5f;

        private bool ContainsLoad(Vector3 point)
        {
            if (!Valid) return false;
            var p = coordinates.Local(point); var min = local.min; var max = local.max;
            // Footprint probes inset each wall by 2 cm. That safety inset must not
            // reject a barrel resting against the wall's inside face. Restore only
            // that margin plus 2 mm for solver contact, keeping height unchanged.
            const float contact = 0.022f;
            return p.x >= min.x - contact && p.x <= max.x + contact &&
                p.y >= min.y && p.y <= max.y && p.z >= min.z - contact && p.z <= max.z + contact;
        }

        internal bool ContainsContact(Vector3 point)
        {
            if (!Valid) return false;
            var p = coordinates.Local(point); var min = local.min; var max = local.max;
            // The measured cavity is inset from its physical walls by 2 cm. A
            // contact on that shell (including small solver penetration) is inside;
            // a protruding load striking the outer chassis is not an internal hit.
            const float shell = 0.05f;
            return p.x >= min.x - shell && p.x <= max.x + shell &&
                p.y >= min.y - shell && p.y <= max.y + shell && p.z >= min.z - shell && p.z <= max.z + shell;
        }

        // Collider queries run at physics poses, while vehicle/item render transforms
        // may be interpolated behind them. Never mix those two coordinate frames.
        private readonly struct Coordinates
        {
            private readonly Transform frame;
            private readonly Rigidbody? body;
            internal Coordinates(Transform frame) { this.frame = frame; body = frame.GetComponent<Rigidbody>(); }
            private Quaternion Rotation => body != null ? body.rotation : frame.rotation;
            private Vector3 Position => body != null ? body.position : frame.position;
            internal Vector3 Direction(Vector3 value) => Rotation * value;
            internal Vector3 World(Vector3 value) => Position + Rotation * Vector3.Scale(value, frame.lossyScale);
            internal Vector3 Local(Vector3 value)
            {
                var p = Quaternion.Inverse(Rotation) * (value - Position); var scale = frame.lossyScale;
                return new Vector3(p.x / scale.x, p.y / scale.y, p.z / scale.z);
            }
        }

        internal static bool TryDropSpace(ItemCarriers carriers, InteractableItems item,
            out ulong id, out Transform frame, out VehicleCargoSpace volume)
        {
            var origin = item.itemCollider.bounds.center;
            foreach (var carrier in carriers.Frames())
            {
                var root = carrier.Frame;
                var physics = new Coordinates(root); var up = physics.Direction(Vector3.up);
                if (root.GetComponent<Wheelbarrow>() == null && DefinedBounds?.Invoke(root) is Bounds[] defined && defined.Length > 0)
                {
                    // A drop released above the box still counts while it falls in; it freezes
                    // only once it lies fully inside the box itself.
                    var load = physics.Local(origin);
                    foreach (var box in defined)
                    {
                        var catchment = new Bounds(box.center + Vector3.up * (DefinedDropHeight * .5f), box.size + Vector3.up * DefinedDropHeight);
                        if (!catchment.Contains(load)) continue;
                        id = carrier.Id; frame = root; volume = new VehicleCargoSpace(root, null, box, catchment) { defined = true }; return true;
                    }
                    continue;
                }
                if (root.GetComponent<Wheelbarrow>() != null || !Nearest(origin, -up, 2.5f, item, true, out var floor) ||
                    !carriers.OwnsSpace(carrier.Id, floor.collider.transform) || ExteriorSurface(floor.collider.transform, root) ||
                    Vector3.Dot(floor.normal, up) < 0.7f) continue;
                var at = physics.Local(floor.point);
                // Measure tall/stacked loads near their own height, above bed wheel
                // arches. Installed stake sides raise the ceiling with modest headroom.
                // Keep the original allowance for ordinary compartments.
                float ceiling = StakeCeiling(root, physics, at);
                float probeY = Math.Max(at.y + 0.1f, Math.Min(physics.Local(origin).y, ceiling - CargoHeadroom - 0.05f));
                if (!Footprint(root, physics, at, probeY, item, out var min, out var max) &&
                    !Footprint(root, physics, at, at.y + 0.1f, item, out min, out max)) continue;
                min.y = at.y - 0.015f;
                max.y = ceiling;
                if (Nearest(physics.World(at + Vector3.up * 0.05f), up, ceiling - at.y, item, true, out var roof) && roof.collider.transform.IsChildOf(root))
                    max.y = Math.Min(max.y, physics.Local(roof.point).y - 0.02f);
                if (max.y - min.y < 0.15f) continue;
                var bounds = new Bounds((min + max) * 0.5f, max - min);
                var space = new VehicleCargoSpace(root, floor.collider, bounds, bounds);
                if (!space.Contains(origin)) continue;
                id = carrier.Id; frame = root; volume = space; return true;
            }
            id = 0; frame = null!; volume = null!; return false;
        }

#if MONMP_DEVELOPMENT
        // The editor's starting box: the cavity the automatic measurement finds around a
        // world point (floor below it, walls around it), in the frame's local space.
        internal static bool TryMeasure(Transform root, Vector3 point, out Bounds bounds)
        {
            bounds = default;
            var physics = new Coordinates(root); var up = physics.Direction(Vector3.up);
            if (!Nearest(point + up * 0.05f, -up, 2.5f, null, true, out var floor) || !floor.collider.transform.IsChildOf(root) ||
                Vector3.Dot(floor.normal, up) < 0.7f) return false;
            var at = physics.Local(floor.point);
            float ceiling = StakeCeiling(root, physics, at);
            if (!Footprint(root, physics, at, at.y + 0.1f, null, out var min, out var max)) return false;
            min.y = at.y; max.y = Math.Min(ceiling, at.y + 0.6f);
            bounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }
#endif

        private static float StakeCeiling(Transform root, Coordinates physics, Vector3 floor)
        {
            float ceiling = floor.y + 1.5f;
            root.GetComponentsInChildren(false, stakeSides);
            foreach (var side in stakeSides)
            {
                if (side == null || !side.isPlaced || side.itemCollider == null || !side.itemCollider.enabled ||
                    side.itemCollider.isTrigger || !side.gameObject.activeInHierarchy) continue;
                var shape = side.itemCollider;
                var bounds = shape is BoxCollider box ? new Bounds(box.center, box.size) : shape.bounds;
                float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity, top = float.NegativeInfinity;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    if (shape is BoxCollider) point = shape.transform.TransformPoint(point);
                    var local = shape is BoxCollider ? root.InverseTransformPoint(point) : physics.Local(point);
                    minZ = Math.Min(minZ, local.z); maxZ = Math.Max(maxZ, local.z); top = Math.Max(top, local.y);
                }
                // A bed addon cannot enlarge a separate cabin compartment.
                if (floor.z >= minZ && floor.z <= maxZ) ceiling = Math.Max(ceiling, top + CargoHeadroom);
            }
            stakeSides.Clear();
            return ceiling;
        }

        private static bool Footprint(Transform root, Coordinates physics, Vector3 at, float height,
            InteractableItems? item, out Vector3 min, out Vector3 max)
        {
            var probe = physics.World(new Vector3(at.x, height, at.z));
            min = max = at;
            int walls = 0;
            foreach (var direction in horizontal)
            {
                float edge;
                if (Nearest(probe, physics.Direction(direction), 4f, item, true, out var wall) && wall.collider.transform.IsChildOf(root))
                {
                    var point = physics.Local(wall.point);
                    edge = direction.x != 0 ? point.x : point.z;
                    walls++;
                }
                else
                {
                    // Open doors/tailgates stop at the actual floor edge.
                    float extent = 0;
                    for (float step = 0.1f; step <= 4f; step += 0.1f)
                    {
                        var sample = at + direction * step + Vector3.up * 0.3f;
                        if (!Nearest(physics.World(sample), physics.Direction(Vector3.down), 0.45f, item, true, out var under) ||
                            !under.collider.transform.IsChildOf(root) || Math.Abs(physics.Local(under.point).y - at.y) > 0.12f ||
                            Vector3.Dot(under.normal, physics.Direction(Vector3.up)) < 0.7f) break;
                        extent = step;
                    }
                    edge = (direction.x != 0 ? at.x : at.z) + (direction.x + direction.z) * extent;
                }
                if (direction.x < 0) min.x = edge + 0.02f;
                else if (direction.x > 0) max.x = edge - 0.02f;
                else if (direction.z < 0) min.z = edge + 0.02f;
                else max.z = edge - 0.02f;
            }
            return walls >= 3 && max.x - min.x >= 0.15f && max.z - min.z >= 0.15f;
        }

        private static bool ExteriorSurface(Transform surface, Transform frame)
        {
            // Reviewed native collision hierarchies identify roof/hood shells by
            // these role names. A roof rack or raised fenders must not create an
            // apparent cavity on an exterior painted surface.
            for (var part = surface; part != null && part != frame; part = part.parent)
                if (part.name.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    part.name.IndexOf("Hood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    part.name.IndexOf("Bonnet", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal bool FullyInside(InteractableItems item) => FullyInside(item, false);
        internal bool FullyInsideContactShell(InteractableItems item) => FullyInside(item, true);

        private bool FullyInside(InteractableItems item, bool contactShell)
        {
            if (!Valid) return false;
            bool any = false;
            // Refresh topology on every observation: native packing/part changes
            // may replace child shapes while the drop is still settling.
            item.GetComponentsInChildren(false, itemShapes);
            foreach (var collider in itemShapes)
            {
                if (!collider.enabled || collider.isTrigger || collider.GetComponentInParent<InteractableItems>() != item) continue;
                any = true;
                if (collider is MeshCollider mesh && mesh.sharedMesh != null && mesh.sharedMesh.isReadable)
                {
                    // Native barrels have a readable 42-vertex collision hull. Its
                    // world AABB corners contain empty air, especially when tilted,
                    // and falsely reject otherwise contained barrels beside a wall.
                    if (cachedMesh != mesh.sharedMesh)
                    { mesh.sharedMesh.GetVertices(meshVertices); cachedMesh = mesh.sharedMesh; }
                    if (!MeshInside(item, collider, contactShell)) return false;
                    continue;
                }
                bool oriented = TryShapeBounds(collider, out var bounds);
                if (!oriented) bounds = collider.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                    if (oriented)
                    {
                        point = collider.transform.TransformPoint(point);
                        if (item.rb != null) point = item.rb.position + item.rb.rotation *
                            (Quaternion.Inverse(item.transform.rotation) * (point - item.transform.position));
                    }
                    if (!(contactShell ? ContainsContact(point) : ContainsLoad(point)) || !FloorUnder(point, item)) return false;
                }
            }
            return any;
        }

        // The collider's own box, carried by its transform. A world AABB of a tilted log,
        // capsule or unreadable mesh adds empty corners that reach through the bed walls
        // (a maple log lying diagonally never froze); barrels use their readable hull above.
        internal static bool TryShapeBounds(Collider collider, out Bounds bounds)
        {
            switch (collider)
            {
                case BoxCollider box: bounds = new Bounds(box.center, box.size); return true;
                case SphereCollider sphere:
                    float diameter = sphere.radius * 2f;
                    bounds = new Bounds(sphere.center, new Vector3(diameter, diameter, diameter)); return true;
                case CapsuleCollider capsule:
                    float width = capsule.radius * 2f, length = Math.Max(capsule.height, width);
                    bounds = new Bounds(capsule.center, new Vector3(capsule.direction == 0 ? length : width,
                        capsule.direction == 1 ? length : width, capsule.direction == 2 ? length : width)); return true;
                case MeshCollider mesh when mesh.sharedMesh != null: bounds = mesh.sharedMesh.bounds; return true;
                default: bounds = default; return false;
            }
        }

        private bool MeshInside(InteractableItems item, Collider collider, bool contactShell)
        {
            if (meshVertices.Count == 0) return false;
            Vector3 minX = default, maxX = default, minZ = default, maxZ = default;
            float xMin = float.PositiveInfinity, xMax = float.NegativeInfinity, zMin = float.PositiveInfinity, zMax = float.NegativeInfinity;
            foreach (var vertex in meshVertices)
            {
                var point = collider.transform.TransformPoint(vertex);
                if (item.rb != null) point = item.rb.position + item.rb.rotation *
                    (Quaternion.Inverse(item.transform.rotation) * (point - item.transform.position));
                if (!(contactShell ? ContainsContact(point) : ContainsLoad(point))) return false;
                var localPoint = coordinates.Local(point);
                if (localPoint.x < xMin) { xMin = localPoint.x; minX = point; }
                if (localPoint.x > xMax) { xMax = localPoint.x; maxX = point; }
                if (localPoint.z < zMin) { zMin = localPoint.z; minZ = point; }
                if (localPoint.z > zMax) { zMax = localPoint.z; maxZ = point; }
            }
            // Check physical footprint extremes, rather than every hull vertex or
            // the empty corners of the barrel's bounding box.
            return FloorUnder(minX, item) && FloorUnder(maxX, item) && FloorUnder(minZ, item) && FloorUnder(maxZ, item);
        }

        private bool FloorUnder(Vector3 point, InteractableItems self)
        {
            var projected = coordinates.Local(point);
            // Cast from the load, rather than below a wheel arch or an existing
            // stack. Different corners can be supported by different bed levels.
            float distance = Math.Max(0.5f, projected.y - local.min.y + 1.5f);
            return Nearest(point + coordinates.Direction(Vector3.up) * 0.04f, coordinates.Direction(Vector3.down), distance, self, true, out var support) &&
                support.collider.transform.IsChildOf(frame) && !ExteriorSurface(support.collider.transform, frame) &&
                Vector3.Dot(support.normal, coordinates.Direction(Vector3.up)) >= 0.7f;
        }

        private static bool Nearest(Vector3 origin, Vector3 direction, float distance, InteractableItems? self, bool skipCargo, out RaycastHit closest)
        {
            int count; RaycastHit[] complete;
            while (true)
            {
                count = Physics.RaycastNonAlloc(origin, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
                if (count < hits.Length) { complete = hits; break; }
                Array.Clear(hits, 0, count);
                if (hits.Length >= 4096) { complete = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Ignore); count = complete.Length; break; }
                Array.Resize(ref hits, hits.Length * 2);
            }
            float nearest = float.PositiveInfinity; closest = default;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var hit = complete[i];
                    if (hit.collider == null || self != null && hit.collider.transform.IsChildOf(self.transform) || hit.distance >= nearest) continue;
                    var item = skipCargo ? hit.collider.GetComponentInParent<InteractableItems>() : null;
                    if (item != null && !(item is InteractableParts part && part.isPlaced) && !(item is Hardware_Bolt bolt && bolt.isPlaced)) continue;
                    closest = hit; nearest = hit.distance;
                }
                return nearest < float.PositiveInfinity;
            }
            finally { Array.Clear(complete, 0, count); }
        }

        internal static bool Supported(InteractableItems item, Transform frame, ItemCarriers? carriers = null)
        {
            var bounds = item.itemCollider.bounds;
            return Nearest(bounds.center, Vector3.down, bounds.extents.y + 0.08f, item, false, out var support) &&
                (support.collider.transform.IsChildOf(frame) || carriers?.IsSupport(support.collider.transform, frame) == true) &&
                Vector3.Dot(support.normal, Vector3.up) > 0.35f;
        }
    }
}
