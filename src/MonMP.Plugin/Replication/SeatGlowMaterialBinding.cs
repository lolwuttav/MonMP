using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Renderer.material(s) and native hover restoration can detach the private materials.
    // Keep the partition's slots attached to the materials whose emission we actually edit.
    internal sealed class SeatGlowMaterialBinding
    {
        private readonly MeshRenderer renderer;
        private readonly Material[] materials;
        private readonly int firstOwnedSlot;
        private readonly List<Material> current = new List<Material>();

        internal SeatGlowMaterialBinding(MeshRenderer renderer, Material[] materials, int firstOwnedSlot)
        {
            if (firstOwnedSlot < 1 || firstOwnedSlot >= materials.Length) throw new System.ArgumentOutOfRangeException(nameof(firstOwnedSlot));
            this.renderer = renderer;
            this.firstOwnedSlot = firstOwnedSlot;
            this.materials = (Material[])materials.Clone();
            renderer.sharedMaterials = this.materials;
        }

        internal bool IsApplied
        {
            get
            {
                if (renderer == null) return false;
                current.Clear();
                renderer.GetSharedMaterials(current);
                if (current.Count != materials.Length) return false;
                for (int i = firstOwnedSlot; i < materials.Length; i++)
                    if (current[i] != materials[i]) return false;
                return true;
            }
        }

        internal void Restore()
        {
            if (renderer == null || IsApplied) return;
            // Native slots may have legitimately become renderer-local instances. Keep them;
            // only the partition regions have to refer to our emission targets.
            for (int i = 0; i < firstOwnedSlot && i < current.Count; i++) materials[i] = current[i];
            renderer.sharedMaterials = materials;
        }

        internal Material NativeMaterial(int slot) => materials[slot];

        // Native paint/save loading edits only the original material indices. Mirror those
        // properties onto the partition slots on that mutation, never in the idle frame loop.
        internal void RefreshAppearance(bool[] lit, Color hoverColor)
        {
            if (renderer == null) return;
            current.Clear();
            renderer.GetSharedMaterials(current);
            bool detached = current.Count != materials.Length;
            for (int i = 0; i < firstOwnedSlot && i < current.Count; i++) materials[i] = current[i];
            for (int i = firstOwnedSlot; i < materials.Length; i++)
            {
                var original = materials[i % firstOwnedSlot];
                var owned = materials[i];
                owned.shader = original.shader;
                owned.CopyPropertiesFromMaterial(original);
                if (lit[i / firstOwnedSlot - 1])
                {
                    owned.EnableKeyword("_EMISSION");
                    owned.SetColor("_EmissionColor", hoverColor);
                }
                if (i >= current.Count || current[i] != owned) detached = true;
            }
            if (detached) renderer.sharedMaterials = materials;
        }
    }
}
