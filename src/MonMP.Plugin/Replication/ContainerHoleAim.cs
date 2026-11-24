using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class ContainerHoleAim
    {
        // Native can/pump pours cast only against ContainerHole. The crosshair mask
        // excludes that layer, so its ordinary first hit cannot protect a filler from
        // either a native seat collider or MonMP's wider synthetic seat selection.
        internal static bool Target(Ray ray)
        {
            int layer = LayerMask.NameToLayer("ContainerHole");
            return layer >= 0 && Physics.Raycast(ray, out var hit, 2.4f, 1 << layer, QueryTriggerInteraction.Collide) &&
                hit.collider.CompareTag("ContainerHole");
        }
    }
}
