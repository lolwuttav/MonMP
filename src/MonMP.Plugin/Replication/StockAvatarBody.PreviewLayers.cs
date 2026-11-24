#if MONMP_DEVELOPMENT
using System;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class StockAvatarBody
    {
        // Flat white/black native Standard masks retain depth for every surface.
        // The offline baker takes colour from full native renders only where this
        // mask is white, preserving mutual skin/hair/garment occlusion exactly.
        internal void CaptureUpperMask(bool fabric, Action capture)
        {
            var actor = modularParts.Keys.OfType<SkinnedMeshRenderer>().First(r => r.name == "Actor");
            var original = actor.sharedMesh;
            var states = body.GetComponentsInChildren<Renderer>(true)
                .Select(r => (Renderer: r, Materials: r.sharedMaterials)).ToArray();
            Material? white = null, black = null; Mesh? visible = null, hidden = null; GameObject? remainder = null;
            try
            {
                var shader = Shader.Find("Standard") ?? throw new InvalidOperationException("Preview mask shader missing.");
                white = new Material(shader) { color = Color.white }; black = new Material(shader) { color = Color.black };
                foreach (var material in new[] { white, black })
                {
                    material.SetFloat("_Metallic", 0); material.SetFloat("_Glossiness", 0);
                    material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                    material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", material == white ? Color.white : Color.black);
                }
                if (!fabric)
                {
                    visible = UnityEngine.Object.Instantiate(original);
                    visible.SetTriangles(modularSkinFaces[0].Concat(modularSkinFaces[1]).ToArray(), 0, false);
                    actor.sharedMesh = visible;
                    hidden = UnityEngine.Object.Instantiate(original);
                    hidden.SetTriangles(modularSkinFaces[2].Concat(modularSkinFaces[3]).ToArray(), 0, false);
                    remainder = new GameObject("MonMP preview lower skin depth");
                    remainder.transform.SetParent(actor.transform, false);
                    var skin = remainder.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = hidden; skin.bones = actor.bones; skin.rootBone = actor.rootBone;
                    skin.sharedMaterials = new[] { black }; skin.updateWhenOffscreen = true;
                }
                foreach (var state in states)
                {
                    var renderer = state.Renderer;
                    bool selected = fabric ? renderer == shirtRenderer : renderer == actor ||
                        modularParts.TryGetValue(renderer, out int part) && part <= 1 && renderer != shirtRenderer;
                    renderer.sharedMaterials = state.Materials.Select(_ => selected ? white : black).ToArray();
                }
                capture();
            }
            finally
            {
                actor.sharedMesh = original;
                foreach (var state in states) if (state.Renderer != null) state.Renderer.sharedMaterials = state.Materials;
                if (remainder != null) UnityEngine.Object.DestroyImmediate(remainder);
                if (visible != null) UnityEngine.Object.DestroyImmediate(visible);
                if (hidden != null) UnityEngine.Object.DestroyImmediate(hidden);
                if (white != null) UnityEngine.Object.DestroyImmediate(white);
                if (black != null) UnityEngine.Object.DestroyImmediate(black);
            }
        }
    }
}
#endif
