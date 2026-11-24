using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using NVector3 = System.Numerics.Vector3;

namespace MonMP.Plugin.Replication
{
    // Owns a private mesh/material partition on the existing renderer, never an overlay. Shared
    // source assets are read only and every replacement is restored when the session ends.
    internal sealed class SeatSurfaceGlow : IDisposable
    {
        private readonly MeshFilter filter;
        private readonly MeshRenderer renderer;
        private readonly Mesh originalMesh;
        private readonly Material[] originalMaterials;
        private readonly List<Material> ownedMaterials = new List<Material>();
        private readonly List<(Material Material, Material Original)>[] regionMaterials;
        private readonly bool[] lit;
        private Mesh? replacement;
        private SeatGlowMaterialBinding? materialBinding;
        private readonly Action paintChanged;
        private bool disposed;

        internal bool IsLit(int region) => !disposed && renderer != null && renderer.enabled &&
            filter.sharedMesh == replacement && materialBinding?.IsApplied == true && lit[region];

        internal static SeatSurfaceGlow Create(MeshRenderer renderer, Transform vehicle, Bounds[] regions)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("Native seat surface readback requires Direct3D 11.");
            var glow = new SeatSurfaceGlow(renderer, regions.Length);
            try { glow.Build(vehicle, regions); SeatGlowPaint.Bind(renderer, glow.paintChanged); return glow; }
            catch { glow.Dispose(); throw; }
        }

        private SeatSurfaceGlow(MeshRenderer renderer, int regions)
        {
            this.renderer = renderer;
            paintChanged = RefreshPaint;
            filter = renderer.GetComponent<MeshFilter>();
            originalMesh = filter.sharedMesh;
            originalMaterials = renderer.sharedMaterials;
            regionMaterials = Enumerable.Range(0, regions).Select(_ => new List<(Material, Material)>()).ToArray();
            lit = new bool[regions];
        }

        private void Build(Transform vehicle, Bounds[] bounds)
        {
            var source = originalMesh;
            if (renderer.isPartOfStaticBatch || source.vertexCount > 100000 || source.subMeshCount != originalMaterials.Length)
                throw new InvalidOperationException("Unsupported seat surface layout.");
            var attributes = source.GetVertexAttributes();
            if (attributes.Any(a => a.format != VertexAttributeFormat.Float32 && a.format != VertexAttributeFormat.Float16))
                throw new InvalidOperationException("Unsupported seat surface vertex format.");
            var strides = Enumerable.Range(0, source.vertexBufferCount).Select(source.GetVertexBufferStride).ToArray();
            var raw = Enumerable.Range(0, source.vertexBufferCount).Select(i => SeatGlowMesh.VertexBytes(source, i)).ToArray();
            for (int i = 0; i < raw.Length; i++)
                if (raw[i].Length != source.vertexCount * strides[i]) throw new InvalidOperationException("Seat vertex buffer size mismatch.");
            var output = raw.Select(b => new List<byte>(b)).ToArray();
            var indexBytes = SeatGlowMesh.IndexBytes(source);
            int width = source.indexFormat == IndexFormat.UInt16 ? 2 : 4;
            int[] indices = Enumerable.Range(0, indexBytes.Length / width).Select(i => width == 2 ?
                (int)BitConverter.ToUInt16(indexBytes, i * width) : checked((int)BitConverter.ToUInt32(indexBytes, i * width))).ToArray();
            if (indices.Length == 0 || indices.All(i => i == 0)) throw new InvalidOperationException("Seat index readback returned no geometry.");
            var positions = new NVector3[source.vertexCount];
            int positionStream = source.GetVertexAttributeStream(VertexAttribute.Position);
            int positionOffset = source.GetVertexAttributeOffset(VertexAttribute.Position);
            if (source.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                throw new InvalidOperationException("Unsupported seat position format.");
            for (int i = 0; i < positions.Length; i++)
            {
                int at = i * strides[positionStream] + positionOffset;
                var data = raw[positionStream];
                var p = vehicle.InverseTransformPoint(renderer.transform.TransformPoint(new Vector3(
                    BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8))));
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) throw new InvalidOperationException("Invalid seat vertices.");
                positions[i] = new NVector3(p.x, p.y, p.z);
            }
            var regions = bounds.Select(b => new SeatGlowPartition.Region(ToNumerics(b.min), ToNumerics(b.max))).ToArray();
            int originalSlots = source.subMeshCount;
            var triangles = Enumerable.Range(0, originalSlots * (regions.Length + 1)).Select(_ => new List<int>()).ToArray();
            var regionCounts = new int[regions.Length];
            int vertexCount = source.vertexCount;
            for (int sub = 0; sub < originalSlots; sub++)
            {
                var descriptor = source.GetSubMesh(sub);
                if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount % 3 != 0)
                    throw new InvalidOperationException("Seat surface is not triangles.");
                if (originalMaterials[sub] == null || !originalMaterials[sub].HasProperty("_EmissionColor"))
                    throw new InvalidOperationException("Seat material has no native emission.");
                for (int at = descriptor.indexStart; at < descriptor.indexStart + descriptor.indexCount; at += 3)
                {
                    int a = indices[at] + descriptor.baseVertex, b = indices[at + 1] + descriptor.baseVertex, c = indices[at + 2] + descriptor.baseVertex;
                    if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length)
                        throw new InvalidOperationException("Seat index out of bounds.");
                    var cache = new Dictionary<NVector3, int>();
                    int Vertex(SeatGlowPartition.Point p)
                    {
                        if (p.Weights == NVector3.UnitX) return a;
                        if (p.Weights == NVector3.UnitY) return b;
                        if (p.Weights == NVector3.UnitZ) return c;
                        if (cache.TryGetValue(p.Weights, out int found)) return found;
                        int result = vertexCount++;
                        if (vertexCount > 250000) throw new InvalidOperationException("Seat partition exceeded its geometry budget.");
                        for (int stream = 0; stream < raw.Length; stream++)
                        {
                            var bytes = new byte[strides[stream]];
                            Array.Copy(raw[stream], a * strides[stream], bytes, 0, bytes.Length);
                            foreach (var attribute in attributes.Where(attribute => attribute.stream == stream))
                            {
                                int offset = source.GetVertexAttributeOffset(attribute.attribute);
                                int size = attribute.format == VertexAttributeFormat.Float32 ? 4 : 2;
                                for (int component = 0; component < attribute.dimension; component++)
                                {
                                    int address = offset + component * size;
                                    float Read(int index) => size == 4 ? BitConverter.ToSingle(raw[stream], index * strides[stream] + address) :
                                        Mathf.HalfToFloat(BitConverter.ToUInt16(raw[stream], index * strides[stream] + address));
                                    float value = Read(a) * p.Weights.X + Read(b) * p.Weights.Y + Read(c) * p.Weights.Z;
                                    var encoded = size == 4 ? BitConverter.GetBytes(value) : BitConverter.GetBytes(Mathf.FloatToHalf(value));
                                    Array.Copy(encoded, 0, bytes, address, size);
                                }
                            }
                            output[stream].AddRange(bytes);
                        }
                        cache.Add(p.Weights, result);
                        return result;
                    }
                    SeatGlowPartition.Triangle(positions[a], positions[b], positions[c], regions, (region, p, q, r) =>
                    {
                        int slot = region < 0 ? sub : originalSlots * (region + 1) + sub;
                        triangles[slot].Add(Vertex(p)); triangles[slot].Add(Vertex(q)); triangles[slot].Add(Vertex(r));
                        if (region >= 0) regionCounts[region]++;
                    });
                }
            }
            if (regionCounts.Any(count => count == 0)) throw new InvalidOperationException("A seat region has no physical surface.");
            replacement = new Mesh { name = "MonMP Seat Glow Surface", indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            replacement.SetVertexBufferParams(vertexCount, attributes);
            for (int stream = 0; stream < output.Length; stream++)
                replacement.SetVertexBufferData(output[stream].ToArray(), 0, 0, output[stream].Count, stream);
            replacement.subMeshCount = triangles.Length;
            var materials = new Material[triangles.Length];
            Array.Copy(originalMaterials, materials, originalSlots);
            for (int slot = 0; slot < triangles.Length; slot++)
            {
                replacement.SetTriangles(triangles[slot], slot, false);
                if (slot < originalSlots) continue;
                var original = originalMaterials[slot % originalSlots];
                var material = new Material(original) { name = "MonMP Seat Glow " + slot };
                ownedMaterials.Add(material);
                materials[slot] = material;
                regionMaterials[slot / originalSlots - 1].Add((material, original));
            }
            replacement.bounds = originalMesh.bounds;
            // Commit only after every region and all buffers/materials have been validated.
            filter.sharedMesh = replacement;
            materialBinding = new SeatGlowMaterialBinding(renderer, materials, originalSlots);
        }

        private static NVector3 ToNumerics(Vector3 p) => new NVector3(p.x, p.y, p.z);

        private void RefreshPaint()
        {
            if (disposed || renderer == null || filter == null || filter.sharedMesh != replacement || materialBinding == null) return;
            var assets = Singleton<GameAssets>.i;
            materialBinding.RefreshAppearance(lit, assets != null ? assets.Get_Interactable_HoverColor() : default);
            for (int i = 0; i < originalMaterials.Length; i++) originalMaterials[i] = materialBinding.NativeMaterial(i);
            foreach (var region in regionMaterials)
                for (int i = 0; i < region.Count; i++) region[i] = (region[i].Material, originalMaterials[i]);
        }

        internal void Set(int region, bool on)
        {
            if (disposed || renderer == null || filter == null || filter.sharedMesh != replacement) return;
            materialBinding?.Restore();
            var assets = Singleton<GameAssets>.i;
            if (on && assets == null) return;
            lit[region] = on;
            foreach (var pair in regionMaterials[region])
            {
                if (on)
                {
                    pair.Material.EnableKeyword("_EMISSION");
                    pair.Material.SetColor("_EmissionColor", assets!.Get_Interactable_HoverColor());
                }
                else
                {
                    if (pair.Original.IsKeywordEnabled("_EMISSION")) pair.Material.EnableKeyword("_EMISSION");
                    else pair.Material.DisableKeyword("_EMISSION");
                    pair.Material.SetColor("_EmissionColor", pair.Original.GetColor("_EmissionColor"));
                }
            }
        }

#if MONMP_DEVELOPMENT
        internal void TestMaterialRecovery()
        {
            var previousMaterials = renderer.sharedMaterials;
            var clones = previousMaterials.Select(m => new Material(m)).ToArray();
            try
            {
                for (int i = 0; i < lit.Length; i++) Set(i, i == 0);
                renderer.sharedMaterials = clones;
                foreach (var clone in clones) clone.DisableKeyword("_EMISSION");
                if (TestColor(0, true)) throw new InvalidOperationException("Detached glow materials passed visibility validation.");
                Set(0, true);
                for (int i = 0; i < lit.Length; i++)
                    if (!TestColor(i, i == 0)) throw new InvalidOperationException("Glow did not recover from cloned material slots.");
                renderer.sharedMaterials = originalMaterials;
                Set(0, true);
                if (!TestColor(0, true)) throw new InvalidOperationException("Glow did not recover from native original-material restoration.");
            }
            finally
            {
                renderer.sharedMaterials = previousMaterials;
                for (int i = 0; i < lit.Length; i++) Set(i, false);
                foreach (var clone in clones) UnityEngine.Object.Destroy(clone);
            }
        }

        internal bool TestRestored => filter != null && filter.sharedMesh == originalMesh && renderer != null &&
            renderer.sharedMaterials.SequenceEqual(originalMaterials);

        internal bool TestColor(int region, bool expected)
        {
            if (disposed || lit[region] != expected || filter.sharedMesh != replacement || materialBinding?.IsApplied != true) return false;
            var color = Singleton<GameAssets>.i.Get_Interactable_HoverColor();
            return regionMaterials[region].All(pair => pair.Material.shader == pair.Original.shader &&
                pair.Material.mainTexture == pair.Original.mainTexture &&
                pair.Material.color == pair.Original.color && pair.Material.mainTextureScale == pair.Original.mainTextureScale &&
                pair.Material.mainTextureOffset == pair.Original.mainTextureOffset &&
                pair.Material.IsKeywordEnabled("_EMISSION") == (expected || pair.Original.IsKeywordEnabled("_EMISSION")) &&
                pair.Material.GetColor("_EmissionColor") == (expected ? color : pair.Original.GetColor("_EmissionColor")));
        }
#endif

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SeatGlowPaint.Unbind(renderer, paintChanged);
            if (filter != null && replacement != null && filter.sharedMesh == replacement)
            {
                filter.sharedMesh = originalMesh;
                if (renderer != null) renderer.sharedMaterials = originalMaterials;
            }
            foreach (var material in ownedMaterials) if (material != null) UnityEngine.Object.Destroy(material);
            if (replacement != null) UnityEngine.Object.Destroy(replacement);
        }
    }
}
