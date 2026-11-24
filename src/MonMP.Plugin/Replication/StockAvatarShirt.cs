using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    // A lease on our remapped stock mesh and an avatar-local native cloth material.
    // Imported meshes discard CPU vertex/index data in the player build. Reading
    // the original GPU buffers once avoids baking a posed mesh into bind space,
    // replacing the 33-bone rig, or distributing any native geometry or textures.
    internal sealed class StockAvatarShirt : IDisposable
    {
        private sealed class Geometry
        {
            internal readonly Mesh Source, Mesh;
            internal int References;
            internal Geometry(Mesh source, Mesh mesh) { Source = source; Mesh = mesh; }
        }

        private static readonly Dictionary<Mesh, Geometry> geometries = new Dictionary<Mesh, Geometry>();
#if MONMP_DEVELOPMENT
        internal static int CachedMeshCount => geometries.Count;
        internal static int CachedLeaseCount
        {
            get { int count = 0; foreach (var entry in geometries.Values) count += entry.References; return count; }
        }
#endif
        private static readonly FieldInfo? nativeShirt = AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt");
        private readonly SkinnedMeshRenderer renderer;
        private readonly Mesh originalMesh;
        private readonly Material originalMaterial;
        private readonly Geometry geometry;
        private bool disposed;
        internal Material Material { get; }
        internal Mesh NativeMesh => originalMesh;

        private StockAvatarShirt(SkinnedMeshRenderer renderer, Material material, Geometry geometry)
        {
            this.renderer = renderer; this.geometry = geometry; Material = material;
            originalMesh = renderer.sharedMesh; originalMaterial = renderer.sharedMaterial;
        }

        internal static bool TryCreate(SkinnedMeshRenderer renderer, Color selected,
            out StockAvatarShirt? owner, out string reason)
        {
            owner = null;
            Geometry? acquired = null;
            Material? material = null;
            try
            {
                if (renderer == null || renderer.sharedMesh == null)
                    throw new InvalidOperationException("Stock shirt renderer is not ready.");
                var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
                var hands = gameplay != null && gameplay.Player != null ? gameplay.Player.Get_VisualHands : null;
                var firstPerson = hands != null ? nativeShirt?.GetValue(hands) as Renderer : null;
                var cloth = firstPerson != null ? firstPerson.sharedMaterial : null;
                if (cloth == null || cloth.mainTexture == null || !cloth.HasProperty("_BumpMap") || cloth.GetTexture("_BumpMap") == null)
                    throw new InvalidOperationException("Native first-person shirt fabric is not ready.");
                var source = renderer.sharedMesh;
                if (!geometries.TryGetValue(source, out acquired))
                {
                    acquired = new Geometry(source, Remap(source));
                    geometries.Add(source, acquired);
                }
                acquired.References++;
                // Native ChangeShirt changes only _Color; copying the complete
                // material keeps its texture/normal map, keywords and response.
                material = new Material(cloth) { name = "MonMP Stock Shirt Fabric", color = selected };
                var created = new StockAvatarShirt(renderer, material, acquired);
                try
                {
                    renderer.sharedMesh = acquired.Mesh;
                    renderer.sharedMaterial = material;
                }
                catch
                {
                    renderer.sharedMesh = created.originalMesh;
                    renderer.sharedMaterial = created.originalMaterial;
                    throw;
                }
                owner = created;
                reason = "native plaid (" + source.vertexCount + " to " + acquired.Mesh.vertexCount + " seam vertices, " + renderer.bones.Length + " original bones)";
                return true;
            }
            catch (Exception ex)
            {
                if (material != null) UnityEngine.Object.Destroy(material);
                if (acquired != null && acquired.References > 0) Release(acquired);
                reason = "stock shirt fabric unavailable: " + ex.Message;
                return false;
            }
        }

        private static Mesh Remap(Mesh source)
        {
            if (source.subMeshCount != 1 || source.blendShapeCount != 0 ||
                source.GetTopology(0) != MeshTopology.Triangles ||
                source.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                source.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                throw new InvalidOperationException("Unsupported stock shirt mesh layout.");

            var descriptors = source.GetVertexAttributes();
            var streams = new byte[source.vertexBufferCount][];
            var strides = new int[streams.Length];
            for (int stream = 0; stream < streams.Length; stream++)
            {
                strides[stream] = source.GetVertexBufferStride(stream);
                streams[stream] = new byte[source.vertexCount * strides[stream]];
                // GetVertexBuffer/GetData never alter source buffer targets,
                // readability, upload settings or the borrowed native mesh.
                using (var buffer = source.GetVertexBuffer(stream)) buffer.GetData(streams[stream]);
            }
            int positionStream = source.GetVertexAttributeStream(VertexAttribute.Position);
            int positionOffset = source.GetVertexAttributeOffset(VertexAttribute.Position);
            var positions = new byte[source.vertexCount * 12];
            for (int i = 0; i < source.vertexCount; i++)
                Buffer.BlockCopy(streams[positionStream], i * strides[positionStream] + positionOffset, positions, i * 12, 12);
            ulong positionHash = StockAvatarShirtUv.Hash(positions);
            bool stock = source.vertexCount == StockAvatarShirtUv.SourceVertexCount && StockAvatarShirtUv.AcceptsPositionHash(positionHash);
            NativeGarmentUv.Map? map = null;
            if (!stock)
            {
                foreach (var candidate in NativeGarmentUv.Maps)
                    if (candidate.Count == source.vertexCount && candidate.PositionHash == positionHash) { map = candidate; break; }
                if (map == null) throw new InvalidOperationException("Native garment geometry differs from the authored fabric maps.");
            }
            int[] sourceVertices = stock ? StockAvatarShirtUv.SourceVertices : map!.Vertices;
            float[] coordinates = stock ? StockAvatarShirtUv.Coordinates : map!.Coordinates;
            int[] triangles = stock ? StockAvatarShirtBacking.Triangles() : map!.Triangles;

            var indices = ReadIndices(source);
            var indexBytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            if (StockAvatarShirtUv.Hash(indexBytes) != (stock ? StockAvatarShirtUv.IndexHash : map!.IndexHash))
                throw new InvalidOperationException("Stock shirt topology differs from the authored fabric map.");
            var bindposes = source.bindposes;
            if (bindposes.Length != 33 && bindposes.Length != 44) throw new InvalidOperationException("Unsupported stock shirt bind poses.");
            var sourceCounts = source.GetBonesPerVertex();
            var sourceWeights = source.GetAllBoneWeights();
            if (!sourceCounts.IsCreated || !sourceWeights.IsCreated || sourceCounts.Length != source.vertexCount)
                throw new InvalidOperationException("Stock shirt skin data is not ready.");
            for (int i = 0; i < sourceWeights.Length; i++)
            {
                var weight = sourceWeights[i];
                if (weight.boneIndex < 0 || weight.boneIndex >= bindposes.Length ||
                    float.IsNaN(weight.weight) || float.IsInfinity(weight.weight) || weight.weight <= 0f || weight.weight > 1f)
                    throw new InvalidOperationException("Invalid stock shirt bone influence.");
            }

            var offsets = new int[sourceCounts.Length];
            int originalWeightCount = 0, weightCount = 0;
            for (int i = 0; i < sourceCounts.Length; i++)
            { offsets[i] = originalWeightCount; originalWeightCount += sourceCounts[i]; }
            if (originalWeightCount != sourceWeights.Length) throw new InvalidOperationException("Invalid stock shirt skin data.");
            foreach (int index in sourceVertices) weightCount += sourceCounts[index];

            var mesh = new Mesh { name = "MonMP Stock Shirt Fabric UV", indexFormat = IndexFormat.UInt16 };
            try
            {
                mesh.SetVertexBufferParams(sourceVertices.Length, descriptors);
                for (int stream = 0; stream < streams.Length; stream++)
                {
                    var duplicated = new byte[sourceVertices.Length * strides[stream]];
                    for (int i = 0; i < sourceVertices.Length; i++)
                        Buffer.BlockCopy(streams[stream], sourceVertices[i] * strides[stream], duplicated, i * strides[stream], strides[stream]);
                    mesh.SetVertexBufferData(duplicated, 0, 0, duplicated.Length, stream,
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                }
                mesh.bindposes = bindposes;
                var counts = new NativeArray<byte>(sourceVertices.Length, Allocator.Temp);
                var weights = new NativeArray<BoneWeight1>(weightCount, Allocator.Temp);
                try
                {
                    int cursor = 0;
                    for (int i = 0; i < sourceVertices.Length; i++)
                    {
                        int original = sourceVertices[i];
                        counts[i] = sourceCounts[original];
                        for (int j = 0; j < counts[i]; j++) weights[cursor++] = sourceWeights[offsets[original] + j];
                    }
                    mesh.SetBoneWeights(counts, weights);
                }
                finally { weights.Dispose(); counts.Dispose(); }
                var uv = new Vector2[sourceVertices.Length];
                for (int i = 0; i < uv.Length; i++)
                    uv[i] = new Vector2(coordinates[i * 2], coordinates[i * 2 + 1]);
                mesh.uv = uv;
                mesh.SetTriangles(triangles, 0, false);
                mesh.bounds = source.bounds;
                // The native fabric's normal map must follow the new UV charts.
                mesh.RecalculateTangents();
                return mesh;
            }
            catch { UnityEngine.Object.Destroy(mesh); throw; }
        }

        private static int[] ReadIndices(Mesh source)
        {
            var submesh = source.GetSubMesh(0);
            var indices = new int[submesh.indexCount];
            using (var buffer = source.GetIndexBuffer())
            {
                if (source.indexFormat == IndexFormat.UInt16)
                {
                    var raw = new ushort[buffer.count]; buffer.GetData(raw);
                    for (int i = 0; i < indices.Length; i++) indices[i] = raw[submesh.indexStart + i] + submesh.baseVertex;
                }
                else
                {
                    var raw = new int[buffer.count]; buffer.GetData(raw);
                    for (int i = 0; i < indices.Length; i++) indices[i] = raw[submesh.indexStart + i] + submesh.baseVertex;
                }
            }
            return indices;
        }

        private static void Release(Geometry geometry)
        {
            if (--geometry.References != 0) return;
            geometries.Remove(geometry.Source);
            if (geometry.Mesh != null) UnityEngine.Object.Destroy(geometry.Mesh);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (renderer != null)
            {
                if (renderer.sharedMesh == geometry.Mesh) renderer.sharedMesh = originalMesh;
                if (renderer.sharedMaterial == Material) renderer.sharedMaterial = originalMaterial;
            }
            UnityEngine.Object.Destroy(Material);
            Release(geometry);
        }
    }
}
