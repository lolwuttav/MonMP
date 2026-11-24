using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    // Private body geometry hides only the skin covered by the long-sleeve shoulders.
    // Cloth, hand/head geometry, materials, vertex attributes and animation remain native.
    internal sealed class StockAvatarSleeveMask : IDisposable
    {
        private sealed class Geometry
        {
            internal readonly Mesh Source, Masked;
            internal int References;
            internal Geometry(Mesh source, Mesh masked) { Source = source; Masked = masked; }
        }
        private static readonly Dictionary<Mesh, Geometry> cache = new Dictionary<Mesh, Geometry>();
        private readonly SkinnedMeshRenderer renderer;
        private readonly Geometry geometry;
        private bool disposed;

        private StockAvatarSleeveMask(SkinnedMeshRenderer renderer, Geometry geometry)
        { this.renderer = renderer; this.geometry = geometry; }

        internal static bool TryCreate(SkinnedMeshRenderer? renderer, out StockAvatarSleeveMask? owner)
        {
            owner = null;
            Geometry? acquired = null;
            try
            {
                if (renderer == null || renderer.name != "Actor" || renderer.sharedMesh == null) return false;
                var source = renderer.sharedMesh;
                if (!cache.TryGetValue(source, out acquired))
                {
                    acquired = new Geometry(source, Build(source));
                    cache.Add(source, acquired);
                }
                acquired.References++;
                var created = new StockAvatarSleeveMask(renderer, acquired);
                renderer.sharedMesh = acquired.Masked;
                owner = created;
                return true;
            }
            catch
            {
                if (acquired != null && acquired.References > 0) Release(acquired);
                return false;
            }
        }

        private static Mesh Build(Mesh source)
        {
            if (source.vertexCount != StockAvatarSleeveCoverage.VertexCount || source.subMeshCount != 1 || source.blendShapeCount != 0 ||
                source.GetTopology(0) != MeshTopology.Triangles || source.GetIndexCount(0) != StockAvatarSleeveCoverage.TriangleCount * 3 ||
                source.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                source.GetVertexAttributeDimension(VertexAttribute.Position) != 3 ||
                (source.bindposes.Length != 33
                    && source.bindposes.Length != 44
                ))
                throw new InvalidOperationException("Unsupported stock body layout.");
            var streams = new byte[source.vertexBufferCount][];
            for (int stream = 0; stream < streams.Length; stream++) streams[stream] = SeatGlowMesh.VertexBytes(source, stream);
            int positionStream = source.GetVertexAttributeStream(VertexAttribute.Position);
            int positionOffset = source.GetVertexAttributeOffset(VertexAttribute.Position);
            int stride = source.GetVertexBufferStride(positionStream);
            var positions = new byte[source.vertexCount * 12];
            for (int i = 0; i < source.vertexCount; i++)
                Buffer.BlockCopy(streams[positionStream], i * stride + positionOffset, positions, i * 12, 12);
            ulong positionHash = StockAvatarShirtUv.Hash(positions);
            if (positionHash != StockAvatarSleeveCoverage.PositionHash
                && !NpcAvatarCoveredSkin.Accepts(positionHash)
                )
                throw new InvalidOperationException("Stock body differs from the sleeve mask.");
            var raw = SeatGlowMesh.IndexBytes(source);
            var submesh = source.GetSubMesh(0);
            var indices = new int[submesh.indexCount];
            int width = source.indexFormat == IndexFormat.UInt16 ? 2 : 4;
            for (int i = 0; i < indices.Length; i++)
            {
                int address = (submesh.indexStart + i) * width;
                indices[i] = checked((width == 2 ? BitConverter.ToUInt16(raw, address) : (int)BitConverter.ToUInt32(raw, address)) + submesh.baseVertex);
            }
            var indexBytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            if (StockAvatarShirtUv.Hash(indexBytes) != StockAvatarSleeveCoverage.IndexHash)
                throw new InvalidOperationException("Stock body topology differs from the sleeve mask.");
            var mesh = new Mesh { name = "MonMP Covered Shoulder Skin", indexFormat = source.indexFormat };
            try
            {
                mesh.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
                for (int stream = 0; stream < streams.Length; stream++)
                    mesh.SetVertexBufferData(streams[stream], 0, 0, streams[stream].Length, stream,
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.bindposes = source.bindposes;
                mesh.SetBoneWeights(source.GetBonesPerVertex(), source.GetAllBoneWeights());
                mesh.SetTriangles(
                    positionHash == NpcAvatarCoveredSkin.PositionHash ? NpcAvatarCoveredSkin.ExcludeCoveredFaces(indices) :
                    StockAvatarSleeveCoverage.ExcludeCoveredFaces(indices), 0, false);
                mesh.bounds = source.bounds;
                return mesh;
            }
            catch { UnityEngine.Object.Destroy(mesh); throw; }
        }

        private static void Release(Geometry geometry)
        {
            if (--geometry.References != 0) return;
            cache.Remove(geometry.Source);
            if (geometry.Masked != null) UnityEngine.Object.Destroy(geometry.Masked);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (renderer != null && renderer.sharedMesh == geometry.Masked) renderer.sharedMesh = geometry.Source;
            Release(geometry);
        }
    }
}
