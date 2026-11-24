using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    // Read the existing GPU mesh without changing import flags or game assets.
    internal static class SeatGlowMesh
    {
        internal static byte[] VertexBytes(Mesh mesh, int stream)
        {
            return D3D11MeshReadback.Read(mesh.GetNativeVertexBufferPtr(stream), checked(mesh.vertexCount * mesh.GetVertexBufferStride(stream)));
        }

        internal static byte[] IndexBytes(Mesh mesh)
        {
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            { var sub = mesh.GetSubMesh(i); count = Math.Max(count, sub.indexStart + sub.indexCount); }
            return D3D11MeshReadback.Read(mesh.GetNativeIndexBufferPtr(), checked(count * (mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4)));
        }

    }
}
