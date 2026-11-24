using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    // Construction-only readback. Borrowed game meshes are never made readable or modified.
    internal sealed class AvatarMeshData
    {
        private static readonly Dictionary<Mesh, AvatarMeshData> cache = new Dictionary<Mesh, AvatarMeshData>();
        internal readonly Mesh Source;
        internal readonly Vector3[] Positions;
        internal readonly Vector2[] Uv;
        internal readonly int[][] Triangles;
        private readonly byte[][] streams;
        private AvatarMeshData(Mesh mesh)
        {
            Source = mesh;
            if (mesh.blendShapeCount != 0 || mesh.vertexCount < 1 || mesh.vertexCount > 10000 ||
                mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                throw new InvalidOperationException("Unsupported native avatar mesh layout.");
            streams = new byte[mesh.vertexBufferCount][];
            for (int i = 0; i < streams.Length; i++)
            {
                streams[i] = new byte[mesh.vertexCount * mesh.GetVertexBufferStride(i)];
                using var buffer = mesh.GetVertexBuffer(i); buffer.GetData(streams[i]);
            }
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position), offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position),
                stride = mesh.GetVertexBufferStride(stream);
            Positions = new Vector3[mesh.vertexCount];
            for (int i = 0; i < Positions.Length; i++)
            {
                int p = i * stride + offset;
                Positions[i] = new Vector3(BitConverter.ToSingle(streams[stream], p), BitConverter.ToSingle(streams[stream], p + 4), BitConverter.ToSingle(streams[stream], p + 8));
            }
            Uv = Array.Empty<Vector2>();
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0) && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) >= 2)
            {
                var format = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
                if (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16)
                    throw new InvalidOperationException("Unsupported native skin UV format: " + format);
                stream = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0); offset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                stride = mesh.GetVertexBufferStride(stream); Uv = new Vector2[mesh.vertexCount];
                for (int i = 0; i < Uv.Length; i++)
                {
                    int address = i * stride + offset;
                    Uv[i] = format == VertexAttributeFormat.Float32
                        ? new Vector2(BitConverter.ToSingle(streams[stream], address), BitConverter.ToSingle(streams[stream], address + 4))
                        : new Vector2(Half(BitConverter.ToUInt16(streams[stream], address)), Half(BitConverter.ToUInt16(streams[stream], address + 2)));
                }
            }
            Triangles = new int[mesh.subMeshCount][];
            using var indices = mesh.GetIndexBuffer();
            var raw16 = mesh.indexFormat == IndexFormat.UInt16 ? new ushort[indices.count] : null;
            var raw32 = raw16 == null ? new int[indices.count] : null;
            if (raw16 != null) indices.GetData(raw16); else indices.GetData(raw32!);
            for (int s = 0; s < Triangles.Length; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) throw new InvalidOperationException("Avatar topology is not triangles.");
                var sub = mesh.GetSubMesh(s); var triangles = Triangles[s] = new int[sub.indexCount];
                for (int i = 0; i < triangles.Length; i++)
                {
                    int index = (raw16 != null ? raw16[sub.indexStart + i] : raw32![sub.indexStart + i]) + sub.baseVertex;
                    if (index < 0 || index >= mesh.vertexCount) throw new InvalidOperationException("Invalid native avatar index.");
                    triangles[i] = index;
                }
            }
        }
        private static float Half(ushort value)
        {
            int exponent = value >> 10 & 31, fraction = value & 1023;
            if (exponent == 31) throw new InvalidOperationException("Non-finite native skin UV.");
            float result = exponent == 0 ? fraction / 16777216f : (1 + fraction / 1024f) * Mathf.Pow(2, exponent - 15);
            return (value & 32768) == 0 ? result : -result;
        }
        internal static AvatarMeshData Read(Mesh mesh)
        {
            if (!cache.TryGetValue(mesh, out var data)) cache.Add(mesh, data = new AvatarMeshData(mesh));
            return data;
        }
        internal static void Clear() => cache.Clear();
        internal Mesh Copy(string name, Vector3[]? positions = null, int[][]? triangles = null)
        {
            var mesh = new Mesh { name = name, indexFormat = Source.indexFormat };
            try
            {
                mesh.SetVertexBufferParams(Source.vertexCount, Source.GetVertexAttributes());
                for (int stream = 0; stream < streams.Length; stream++) mesh.SetVertexBufferData(streams[stream], 0, 0, streams[stream].Length, stream,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                mesh.bindposes = Source.bindposes; mesh.SetBoneWeights(Source.GetBonesPerVertex(), Source.GetAllBoneWeights());
                if (positions != null) mesh.vertices = positions;
                triangles ??= Triangles; mesh.subMeshCount = triangles.Length;
                for (int s = 0; s < triangles.Length; s++) mesh.SetTriangles(triangles[s], s, false);
                if (positions != null) { mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds(); }
                else mesh.bounds = Source.bounds;
                return mesh;
            }
            catch { UnityEngine.Object.Destroy(mesh); throw; }
        }
    }
}
