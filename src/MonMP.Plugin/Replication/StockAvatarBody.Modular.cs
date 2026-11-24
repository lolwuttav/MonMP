using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Protocol;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class StockAvatarBody
    {
        private readonly List<Mesh> modularMeshes = new List<Mesh>();
        private bool modularPrepared;
        private bool nativeShirtTexture;
        private readonly Dictionary<Renderer, int> modularParts = new Dictionary<Renderer, int>();
        private int[][] modularSkinFaces = Array.Empty<int[]>();
        internal static StockAvatarBody? TryCreateModel(Transform parent, Color color, string key, out string reason) =>
            TryCreateAppearance(parent, color, PlayerAppearance.Whole(PlayerModelPolicy.Id(key)), out reason);

        internal static StockAvatarBody? TryCreateAppearance(Transform parent, Color color, PlayerAppearance appearance, out string reason)
        {
            StockAvatarBody? result = null;
            bool parentWasActive = parent.gameObject.activeSelf;
            parent.gameObject.SetActive(false);
            try
            {
                appearance.Validate();
                var head = LoadModel(PlayerModelPolicy.Key(appearance.Head), out reason);
                if (head == null) return null;
                var torso = LoadModel(PlayerModelPolicy.Key(appearance.Torso), out reason);
                if (torso == null) return null;
                var legs = LoadModel(PlayerModelPolicy.Key(appearance.Legs), out reason);
                if (legs == null) return null;
                var shoes = LoadModel(PlayerModelPolicy.Key(appearance.Shoes), out reason);
                if (shoes == null) return null;
                result = Create(parent, color, torso, out reason, appearance.NativeTexture);
                if (result == null) return null;
                result.Assemble(head, torso, legs, shoes, appearance.Torso == 20);
                reason = "modular head=" + appearance.Head + ", torso=" + appearance.Torso + ", legs=" + appearance.Legs + ", shoes=" + appearance.Shoes;
                return result;
            }
            catch (Exception ex)
            {
                result?.Destroy(); reason = "modular avatar unavailable: " + ex.Message; return null;
            }
            finally { parent.gameObject.SetActive(parentWasActive); }
        }
        private static SkinnedMeshRenderer Actor(Library lib) => lib.Template.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .First(r => r.name == "Actor" || r.name == "Actor.001");
        private static bool NativeVisible(Renderer renderer, Library lib)
        {
            if (!renderer.enabled) return false;
            if (lib.UsesModelClothing) return ActiveWithinTemplate(renderer.transform, lib.Template.transform);
            var node = renderer.transform;
            while (node.parent != null && node.parent != lib.Template.transform) node = node.parent;
            return Shown.Contains(node.name);
        }
        private static int ClothingPart(string name) => name.StartsWith("Hair", StringComparison.Ordinal) || NpcAvatarModels.Headwear(name) ? 0 :
            name.StartsWith("Pants_", StringComparison.Ordinal) ? 2 : name.StartsWith("Shoes", StringComparison.Ordinal) ? 3 : 1;
        private void Assemble(Library head, Library torso, Library legs, Library shoes, bool hooded)
        {
            var targets = armature.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            // Retain the torso's single rig. Body parts only contribute renderers, never actors,
            // colliders, animators or a second skeleton. The native end bones also exist on 33-bone rigs.
            foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
            {
                int part = ClothingPart(renderer.name);
                if (part != 1) renderer.gameObject.SetActive(false);
                else if (renderer.enabled && ActiveWithinNpc(renderer.transform)) modularParts[renderer] = 1;
            }
            foreach (var pair in new[] { (Library: head, Part: 0), (Library: legs, Part: 2), (Library: shoes, Part: 3) })
                foreach (var original in pair.Library.Template.GetComponentsInChildren<Renderer>(true))
                {
                    if (ClothingPart(original.name) != pair.Part || !NativeVisible(original, pair.Library)) continue;
                    var copy = UnityEngine.Object.Instantiate(original.gameObject, body.transform, false);
                    copy.name = original.name;
                    // Never instantiate an actor/root here: garment and hair renderer objects only.
                    StripNpcComponents(copy); copy.SetActive(true);
                    var renderer = copy.GetComponent<Renderer>();
                    if (renderer is SkinnedMeshRenderer skin && original is SkinnedMeshRenderer donor)
                    {
                        skin.bones = donor.bones.Select(b => targets.TryGetValue(b.name, out var target) ? target :
                            throw new InvalidOperationException("Part bone missing: " + b.name)).ToArray();
                        skin.rootBone = targets[donor.rootBone.name]; skin.updateWhenOffscreen = true;
                    }
                    else
                    {
                        // LoadModel already places static headwear relative to its bind-pose Head.
                        var donorHead = pair.Library.Template.transform.Find("Armature").GetComponentsInChildren<Transform>(true).First(t => t.name == "Head");
                        var local = donorHead.worldToLocalMatrix * original.transform.localToWorldMatrix;
                        copy.transform.SetParent(targets["Head"], false);
                        copy.transform.localPosition = local.GetColumn(3); copy.transform.localRotation = local.rotation; copy.transform.localScale = local.lossyScale;
                    }
                    modularParts.Add(renderer, pair.Part);
                }
            var actor = body.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Actor");
            // Keep each hairstyle's native scalp fit. A wider torso head otherwise pokes
            // through fitted hair at the temples and rear. Shared vertices blend back
            // into the torso's neck below the scalp, preserving a continuous neckline.
            var sources = new[] { Actor(head), Actor(torso), Actor(legs), Actor(shoes) };
            var data = sources.Select(r => AvatarMeshData.Read(r.sharedMesh)).ToArray();
            var baseline = data[1];
            if (baseline.Positions.Length != StockAvatarSleeveCoverage.VertexCount || baseline.Triangles.Length != 1 ||
                baseline.Triangles[0].Length != StockAvatarSleeveCoverage.TriangleCount * 3 ||
                data.Any(d => d.Positions.Length != baseline.Positions.Length || d.Uv.Length != baseline.Positions.Length || d.Triangles.Length != 1 || !d.Triangles[0].SequenceEqual(baseline.Triangles[0])))
                throw new InvalidOperationException("Native body-part topology differs.");
            var regions = SkinRegions(sources[1], baseline.Positions, hooded);
            var vertices = new Vector3[baseline.Positions.Length];
            var uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                float y = baseline.Positions[i].y;
                // Shared vertices, including UV-seam duplicates, stay connected across the
                // neck/waist/ankles. Smooth transitions are hidden by native collars/waistbands.
                var p = Vector3.Lerp(data[1].Positions[i], data[0].Positions[i], Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1.40f, 1.53f, y)) * regions.Head[i]);
                p = Vector3.Lerp(p, data[2].Positions[i], (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.93f, 1.08f, y))) * regions.Lower[i]);
                vertices[i] = Vector3.Lerp(p, data[3].Positions[i], (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.13f, .24f, y))) * regions.Foot[i]);
                // The fitted female body shares topology/weights but has a different skin
                // atlas layout. Exposed head, leg and foot skin must follow that part's UVs.
                int part = regions.Parts[i];
                uv[i] = data[part].Uv[i];
            }
            var coverings = modularParts.Where(p => p.Value == 1 && p.Key is SkinnedMeshRenderer && p.Key != actor)
                .Select(p => (Part: p.Value, Data: AvatarMeshData.Read(p.Key == shirtRenderer && shirtFabric != null ?
                    shirtFabric.NativeMesh : ((SkinnedMeshRenderer)p.Key).sharedMesh))).ToArray();
            var covered = new bool[vertices.Length];
            var hands = regions.Exposed;
            for (int i = 0; i < vertices.Length; i++)
            {
                // Head and hands remain exposed. Protect by bone influence rather than height:
                // native shoulders can sit above the bottom of the neck. Only the shirt
                // hides underlying skin; native legs and shoes keep their original body fill.
                var p = vertices[i];
                if (hands[i]) continue;
                foreach (var cloth in coverings)
                    if (CoveredByGarment(p, cloth.Data)) { covered[i] = true; break; }
            }
            var faces = new List<int>(); var parts = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
            var indices = baseline.Triangles[0];
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (covered[a] && covered[b] && covered[c]) continue;
                int part = AvatarSkinPartition.Face(regions.Parts[a], regions.Parts[b], regions.Parts[c]);
                faces.Add(a); faces.Add(b); faces.Add(c); parts[part].Add(a); parts[part].Add(b); parts[part].Add(c);
            }
            modularSkinFaces = parts.Select(p => p.ToArray()).ToArray();
            var mesh = baseline.Copy("MonMP Modular Skin", vertices, new[] { faces.ToArray() });
            mesh.uv = uv; mesh.RecalculateTangents();
            modularMeshes.Add(mesh); actor.sharedMesh = mesh; modularParts[actor] = 1;
            modularPrepared = true; RememberNpcHeadwear();
        }
        private static (bool[] Exposed, float[] Head, float[] Lower, float[] Foot, int[] Parts) SkinRegions(SkinnedMeshRenderer renderer, Vector3[] positions, bool hooded)
        {
            var counts = renderer.sharedMesh.GetBonesPerVertex(); var weights = renderer.sharedMesh.GetAllBoneWeights();
            var names = renderer.bones.Select(b => b.name).ToArray();
            var exposed = new bool[counts.Length]; var heads = new float[counts.Length];
            var lower = new float[counts.Length]; var feet = new float[counts.Length]; var parts = new int[counts.Length];
            int cursor = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                float hands = 0, headOnly = 0;
                for (int j = 0; j < counts[i]; j++)
                {
                    var weight = weights[cursor++]; string name = names[weight.boneIndex]; float w = weight.weight;
                    if (name == "Head" || name == "Neck") heads[i] += w;
                    if (name == "Head") headOnly += w;
                    if (name == "Hips" || name.Contains("Leg.") || name.StartsWith("Foot.", StringComparison.Ordinal) || name.StartsWith("Toes.", StringComparison.Ordinal)) lower[i] += w;
                    if (name.StartsWith("Foot.", StringComparison.Ordinal) || name.StartsWith("Toes.", StringComparison.Ordinal)) feet[i] += w;
                    if (name.StartsWith("Hand", StringComparison.Ordinal) || name.StartsWith("Fingers", StringComparison.Ordinal) || name.StartsWith("Thumb", StringComparison.Ordinal)) hands += w;
                }
                // The hood passes near the neck in bind space, but its open front does
                // not cover that skin in posed views. Never cut this neck out of the body.
                exposed[i] = AvatarSkinPartition.Exposed(hands, headOnly, heads[i], hooded);
                parts[i] = AvatarSkinPartition.Part(positions[i].y, heads[i], lower[i], feet[i]);
            }
            return (exposed, heads, lower, feet, parts);
        }
        private static bool CoveredByGarment(Vector3 point, AvatarMeshData garment)
        {
            foreach (var indices in garment.Triangles)
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var a = garment.Positions[indices[i]]; var u = garment.Positions[indices[i + 1]] - a; var v = garment.Positions[indices[i + 2]] - a;
                    var n = Vector3.Cross(u, v); float square = n.sqrMagnitude;
                    if (square < 1e-12f) continue;
                    var d = point - a; float distance = Vector3.Dot(d, n);
                    if (distance * distance > .035f * .035f * square) continue;
                    float uu = Vector3.Dot(u, u), vv = Vector3.Dot(v, v), uv = Vector3.Dot(u, v), du = Vector3.Dot(d, u), dv = Vector3.Dot(d, v);
                    float determinant = uu * vv - uv * uv;
                    float s = (du * vv - dv * uv) / determinant, t = (dv * uu - du * uv) / determinant;
                    if (s >= 0 && t >= 0 && s + t <= 1) return true;
                }
            return false;
        }
        internal void SetNativeShirtTexture(bool native)
        {
            if (native == nativeShirtTexture) return;
            var casting = shadowCasting; bool shadowOnly = casting?.IsShadowOnly ?? false;
            casting?.Restore();
            try
            {
                Color color = ShirtMaterial.color;
                shirtFabric?.Dispose(); shirtFabric = null;
                nativeShirtTexture = native; ShirtMaterial = fallbackShirtMaterial; ShirtMaterial.color = color;
                shirtFabricAttempts = 0; shirtFabricRetryAt = 0;
                TryApplyShirtFabric(); casting?.RefreshMaterials(shirtRenderer);
            }
            finally { if (shadowOnly) casting!.SetShadowOnly(true); }
        }
#if MONMP_DEVELOPMENT
        internal void CaptureModularPart(int part, bool fabric, Action capture, bool headwearOnly = false, bool skinOnly = false)
        {
            var actor = modularParts.Keys.OfType<SkinnedMeshRenderer>().First(r => r.name == "Actor");
            var original = actor.sharedMesh;
            var mesh = UnityEngine.Object.Instantiate(original);
            var states = body.GetComponentsInChildren<Renderer>(true).Select(r => (Renderer: r, Enabled: r.enabled)).ToArray();
            try
            {
                mesh.SetTriangles(fabric || headwearOnly ? Array.Empty<int>() : modularSkinFaces[part], 0, false); actor.sharedMesh = mesh;
                foreach (var state in states)
                {
                    if (state.Renderer == actor) state.Renderer.enabled = !fabric && !headwearOnly;
                    else state.Renderer.enabled = !skinOnly && state.Enabled && modularParts.TryGetValue(state.Renderer, out int p) && p == part &&
                        (state.Renderer == shirtRenderer) == fabric;
                }
                capture();
            }
            finally
            {
                actor.sharedMesh = original;
                foreach (var state in states) if (state.Renderer != null) state.Renderer.enabled = state.Enabled;
                UnityEngine.Object.Destroy(mesh);
            }
        }
#endif
    }
}
