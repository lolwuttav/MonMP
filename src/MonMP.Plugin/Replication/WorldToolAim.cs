using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class WorldToolAim
    {
        private readonly RaycastHit[] occlusionHits = new RaycastHit[32];
        internal AimSegmentTest CheckSegment { get; }
        internal WorldToolAim() { CheckSegment = SegmentClear; }

        // A hand origin is admitted only if the body can see it past real solids. This
        // geometric callback has no authenticated actor/tool identity, so it exempts no
        // solid item, including an item held by the local player or a nearby peer.
        private bool SegmentClear(float ax, float ay, float az, float bx, float by, float bz)
        {
            var from = new Vector3(ax, ay, az);
            var delta = new Vector3(bx, by, bz) - from;
            float distance = delta.magnitude;
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return false;
            if (distance < 0.05f) return true;
            int mask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("Player", "Ignore Raycast", "ContainerHole",
                "SwimmingPlayerSurface", "IgnoreByPlayerInteractable", "BoltHole", "PartSocket");
            int count = Physics.RaycastNonAlloc(from, delta / distance, occlusionHits, distance, mask, QueryTriggerInteraction.Ignore);
            return IndependentAimPolicy.PathClear(count, occlusionHits.Length, i =>
            {
                var collider = occlusionHits[i].collider;
                // A destroyed collider cannot be classified, so it blocks.
                if (collider == null) return true;
                return IndependentAimPolicy.HitBlocks(collider.isTrigger, collider.CompareTag("Player"));
            });
        }
    }
}
