using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // This marker excludes inactive, short-lived effect clones from discovery,
    // including the frame in which Destroy has been requested but not completed.
    internal sealed class ConsumptionEffectRoot : MonoBehaviour { }

    // Renderer.material would silently allocate a private native asset. This owner
    // creates one only for a real color override, and never destroys a borrowed
    // prefab/highlight material if another system swaps the renderer's material.
    internal sealed class FoodMaterialOwner : MonoBehaviour
    {
        private MeshRenderer? renderer;
        private Material? borrowed, owned;

        internal void Apply(MeshRenderer target, Color color)
        {
            if (renderer != target || (owned != null && target.sharedMaterial != owned)) Release();
            renderer = target;
            var current = target.sharedMaterial;
            if (owned != null && borrowed != null && borrowed.color == color) { Release(); return; }
            if (current.color == color) return;
            if (owned == null)
            {
                borrowed = current;
                owned = new Material(current);
                target.sharedMaterial = owned;
            }
            owned.color = color;
        }

        internal void Release()
        {
            var material = owned;
            owned = null;
            if (renderer != null && material != null && renderer.sharedMaterial == material)
                renderer.sharedMaterial = borrowed;
            borrowed = null; renderer = null;
            if (material != null) UnityEngine.Object.Destroy(material);
        }

        private void OnDestroy() => Release();
    }

    internal static class FoodState
    {
        private static readonly FieldInfo guts = AccessTools.Field(typeof(Item_Poutine), "PoutineGuts");
        private static readonly Color spoiled = new Color(0, 0.46f, 0.03f);
        internal static MeshRenderer Renderer(Item_Poutine item) => (MeshRenderer)guts.GetValue(item)!;
        internal static void ValidatePrefab(Item_Poutine item)
        {
            var renderer = Renderer(item);
            if (renderer == null || renderer.sharedMaterial == null ||
                (renderer.transform != item.transform && !renderer.transform.IsChildOf(item.transform)))
                throw new InvalidOperationException("Invalid poutine renderer: " + item.PrefabName);
        }
        internal static void Apply(Item_Poutine item, Item_Poutine asset, ItemValues values, bool hardMode)
        {
            item.Get_IsRefrigerated = (values.Flags & ItemFlags.Refrigerated) != 0;
            var renderer = Renderer(item);
            var color = hardMode && values.Value1 <= 0 ? spoiled : Renderer(asset).sharedMaterial.color;
            ApplyColor(renderer, color);
        }
        internal static void Perished(Item_Poutine item) => ApplyColor(Renderer(item), spoiled);
        private static void ApplyColor(MeshRenderer renderer, Color color)
        {
            var owner = renderer.GetComponent<FoodMaterialOwner>();
            if (owner == null && renderer.sharedMaterial.color == color) return;
            if (owner == null) owner = renderer.gameObject.AddComponent<FoodMaterialOwner>();
            owner.Apply(renderer, color);
        }
        internal static bool Consume(GameObject asset, ItemValues values)
        {
            var root = new GameObject("MonMP consumption effect");
            root.SetActive(false); root.AddComponent<ConsumptionEffectRoot>();
            try
            {
                // Inactive parent prevents Awake/Start, physics and tick subscriptions.
                // The selected vetted item receives the committed values, not defaults.
                var item = UnityEngine.Object.Instantiate(asset, root.transform, false).GetComponent<Item_Poutine>();
                item.isSpawnFromTheSaveLoadSystem = true;
                item.Value1 = values.Value1; item.Get_IsRefrigerated = (values.Flags & ItemFlags.Refrigerated) != 0;
                return item.Use();
            }
            finally { UnityEngine.Object.Destroy(root); }
        }
    }
}
