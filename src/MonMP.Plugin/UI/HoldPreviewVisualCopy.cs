using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Build a live renderer-only hierarchy before baking skins. Evaluating copied
    // bone transforms makes prefab snapshots independent of the asset renderer's
    // pose initialization while preserving its authored bone and renderer scales.
    internal static class HoldPreviewVisualCopy
    {
        private sealed class Skin
        {
            internal SkinnedMeshRenderer Source = null!;
            internal Transform Target = null!;
        }

        internal static void Copy(Transform sourceRoot, Transform targetRoot, int layer, List<Mesh> ownedMeshes, bool equippedPreview = false)
        {
            var nodes = new Dictionary<Transform, Transform>();
            var skins = new List<Skin>();
            var visibility = EquippedPrefabVisibility(sourceRoot, equippedPreview);
            CopyTree(sourceRoot, targetRoot, true);
            // A skin may reference bones later in the tree. Every transform must exist
            // before any copied renderer receives its bones or is baked.
            foreach (var skin in skins) CopySkin(skin, nodes, layer, ownedMeshes);

            void CopyTree(Transform source, Transform target, bool root)
            {
                nodes.Add(source, target);
                target.gameObject.layer = layer;
                if (!root)
                {
                    target.localPosition = source.localPosition;
                    target.localRotation = source.localRotation;
                    target.localScale = source.localScale;
                    target.gameObject.SetActive(visibility.TryGetValue(source.gameObject, out bool active) ? active : source.gameObject.activeSelf);
                }
                var filter = source.GetComponent<MeshFilter>();
                var renderer = source.GetComponent<MeshRenderer>();
                if (filter != null && renderer != null && filter.sharedMesh != null) AddSurface(renderer, target, filter.sharedMesh);
                var skin = source.GetComponent<SkinnedMeshRenderer>();
                if (skin != null && skin.sharedMesh != null) skins.Add(new Skin { Source = skin, Target = target });
                foreach (Transform child in source)
                {
                    var copy = new GameObject(child.name);
                    copy.transform.SetParent(target, false);
                    CopyTree(child, copy.transform, false);
                }
            }
        }

        private static void CopySkin(Skin skin, Dictionary<Transform, Transform> nodes, int layer, List<Mesh> ownedMeshes)
        {
            var holder = new GameObject("Baked skin") { layer = layer };
            holder.transform.SetParent(skin.Target, false);
            var mesh = skin.Source.sharedMesh;
            var sourceBones = skin.Source.bones;
            var bones = new Transform[sourceBones.Length];
            bool complete = sourceBones.Length > 0;
            for (int i = 0; i < sourceBones.Length; i++)
            {
                if (sourceBones[i] == null || !nodes.TryGetValue(sourceBones[i], out var bone)) { complete = false; break; }
                bones[i] = bone;
            }
            Transform? rootBone = null;
            if (skin.Source.rootBone != null && !nodes.TryGetValue(skin.Source.rootBone, out rootBone)) complete = false;
            if (complete)
            {
                // This component exists only on our copy, with only copied bones. A
                // failed or externally-bound skin falls back to its authored rest mesh.
                var baker = skin.Target.gameObject.AddComponent<SkinnedMeshRenderer>();
                var baked = new Mesh();
                try
                {
                    baker.enabled = false;
                    baker.sharedMesh = mesh;
                    baker.bones = bones;
                    baker.rootBone = rootBone;
                    for (int i = 0; i < mesh.blendShapeCount; i++) baker.SetBlendShapeWeight(i, skin.Source.GetBlendShapeWeight(i));
                    // Compensate the renderer scale in the baked vertex coordinates;
                    // the static copy retains that scale in its transform hierarchy.
                    // false leaves the scale in the vertices and applies it twice.
                    baker.BakeMesh(baked, true);
                    if (baked.vertexCount > 0 && baked.vertexCount == mesh.vertexCount)
                    {
                        mesh = baked;
                        ownedMeshes.Add(baked);
                        baked = null!;
                    }
                }
                catch (Exception)
                {
                    // Keep a usable preview without changing source bones or scale.
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baker);
                    if (baked != null) UnityEngine.Object.DestroyImmediate(baked);
                }
            }
            AddSurface(skin.Source, holder.transform, mesh);
        }

        private static void AddSurface(Renderer source, Transform target, Mesh mesh)
        {
            target.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var copy = target.gameObject.AddComponent<MeshRenderer>();
            var materials = source.sharedMaterials;
            copy.sharedMaterials = materials;
            copy.enabled = source.enabled;
            copy.forceRenderingOff = source.forceRenderingOff;
            copy.shadowCastingMode = source.shadowCastingMode;
            copy.receiveShadows = source.receiveShadows;
            var block = new MaterialPropertyBlock();
            source.GetPropertyBlock(block); copy.SetPropertyBlock(block);
            for (int i = 0; i < materials.Length; i++)
            {
                source.GetPropertyBlock(block, i); copy.SetPropertyBlock(block, i);
            }
        }

        private static Dictionary<GameObject, bool> EquippedPrefabVisibility(Transform sourceRoot, bool equippedPreview)
        {
            var result = new Dictionary<GameObject, bool>();
            // Actual scene captures preserve the exact live state. Prefab selections
            // show the native idle equipped branches without running Start/Equiped.
            if (sourceRoot.gameObject.scene.IsValid() && !equippedPreview) return result;
            var tool = sourceRoot.GetComponent<InteractableTools>();
            if (tool == null) return result;
            if (tool is Tool_FishingRod)
            { Set("FishingRod_Folded", false); Set("FishingRod_Unfolded", true); Set("Tube_FishingLine", false); }
            else if (tool is Tool_Envelope)
            { Set("CoverClosed", false); Set("CoverOpened", true); Set("Letter", true); Set("Text_Signature", false); }
            else if (tool is Tool_SprayPaintCan) Set("Cap", false);
            else if (tool is Tool_WindowTintRemover) Set("LameProtecteur", false);
            else if (tool is Tool_HoistPuller) Set("HoistCarAttachment", false);
            else if (tool is Tool_Air_Impact)
            { Set("TubeRolled", false); Set("tubeController", false); Set("Box12mm", true); Set("Box20mm", false); }
            else if (tool is Tool_Air_TirePump)
            { Set("TubeRolled", false); Set("tubeController", false); }
            return result;

            void Set(string fieldName, bool active)
            {
                var value = AccessTools.Field(tool.GetType(), fieldName)?.GetValue(tool);
                var gameObject = value as GameObject ?? (value as Component)?.gameObject;
                if (gameObject != null) result[gameObject] = active;
            }
        }
    }
}
