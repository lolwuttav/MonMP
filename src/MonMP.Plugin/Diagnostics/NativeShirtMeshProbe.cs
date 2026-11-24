using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Diagnostics
{
    internal static class NativeShirtMeshProbe
    {
        internal static string BufferHash(Mesh mesh)
        {
            ulong hash = 14695981039346656037UL;
            void Add(byte[] bytes) { foreach (byte value in bytes) hash = unchecked((hash ^ value) * 1099511628211UL); }
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
            {
                using var buffer = mesh.GetVertexBuffer(stream);
                var bytes = new byte[mesh.vertexCount * mesh.GetVertexBufferStride(stream)]; buffer.GetData(bytes); Add(bytes);
            }
            using (var buffer = mesh.GetIndexBuffer())
            {
                var bytes = new byte[buffer.count * buffer.stride]; buffer.GetData(bytes); Add(bytes);
            }
            return hash.ToString("X16");
        }
        internal static object Run(Gameplay gameplay, string directory)
        {
            var first = (SkinnedMeshRenderer)AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(gameplay.Player.Get_VisualHands);
            var renderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true)
                .Where(r => r == first || r.name == "Shirt_LongSleeve" || r.name == "Shirt_Plaid")
                .Where(r => r.sharedMesh != null).GroupBy(r => r.sharedMesh.GetInstanceID()).Select(g => g.First()).ToArray();
            var results = new List<object>();
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                string name = (renderer == first ? "first-person" : renderer.name) + "-" + mesh.GetInstanceID();
                var item = new Dictionary<string, object?>
                {
                    ["name"] = name, ["path"] = RuntimeProbe.CanonicalPath(renderer.transform), ["mesh"] = mesh.name,
                    ["readable"] = mesh.isReadable, ["vertices"] = mesh.vertexCount, ["bones"] = renderer.bones.Length
                };
                Mesh? clone = null;
                try
                {
                    clone = UnityEngine.Object.Instantiate(mesh);
                    item["cloneReadable"] = clone.isReadable;
                    clone.uv = Enumerable.Repeat(new Vector2(0.3f, 0.4f), mesh.vertexCount).ToArray();
                    item["cloneUvSetterSucceeded"] = clone.uv.Length == mesh.vertexCount && clone.uv[0] == new Vector2(0.3f, 0.4f);
                }
                catch (Exception ex) { item["cloneUvError"] = ex.ToString(); }
                finally { if (clone != null) UnityEngine.Object.Destroy(clone); }
                Try(item, "bindposes", () => mesh.bindposes.Select(m => Enumerable.Range(0, 16).Select(i => m[i]).ToArray()).ToArray());
                Try(item, "boneWeights", () => mesh.boneWeights.Select(w => new { indices = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 }, weights = new[] { w.weight0, w.weight1, w.weight2, w.weight3 } }).ToArray());
                foreach (string method in new[] { "GetBonesPerVertex", "GetAllBoneWeights" })
                    Try(item, method, () =>
                    {
                        object native = AccessTools.Method(typeof(Mesh), method).Invoke(mesh, null);
                        var array = (Array)AccessTools.Method(native.GetType(), "ToArray").Invoke(native, null);
                        return new { length = array.Length, values = array.Cast<object>().ToArray() };
                    });
                Try(item, "attributes", () => mesh.GetVertexAttributes().Select(a => new { attribute = a.attribute.ToString(), format = a.format.ToString(), a.dimension, a.stream }).ToArray());
                for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
                {
                    int index = stream;
                    Try(item, "vertexBuffer" + index, () =>
                    {
                        int stride = mesh.GetVertexBufferStride(index);
                        using var buffer = mesh.GetVertexBuffer(index);
                        var bytes = new byte[mesh.vertexCount * stride]; buffer.GetData(bytes);
                        string file = Path.Combine(directory, name + "-vertex" + index + ".bin"); File.WriteAllBytes(file, bytes);
                        return new { stride, buffer.count, bufferStride = buffer.stride, bytes = bytes.Length, file };
                    });
                }
                Try(item, "indexBuffer", () =>
                {
                    using var buffer = mesh.GetIndexBuffer();
                    Array indices = mesh.indexFormat == IndexFormat.UInt16 ? (Array)new ushort[buffer.count] : new uint[buffer.count];
                    buffer.GetData(indices);
                    var bytes = new byte[indices.Length * (mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4)];
                    Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
                    string file = Path.Combine(directory, name + "-indices.bin"); File.WriteAllBytes(file, bytes);
                    return new { format = mesh.indexFormat.ToString(), buffer.count, buffer.stride, bytes = bytes.Length, file };
                });
                Try(item, "bakedReadable", () =>
                {
                    var baked = new Mesh();
                    try { renderer.BakeMesh(baked); return new { baked.isReadable, baked.vertexCount, uv = baked.uv.Length, weights = baked.boneWeights.Length, bindposes = baked.bindposes.Length }; }
                    finally { UnityEngine.Object.Destroy(baked); }
                });
                results.Add(item);
            }
            return results;
        }
        private static void Try(Dictionary<string, object?> item, string key, Func<object> get)
        {
            try { item[key] = get(); }
            catch (Exception ex) { item[key + "Error"] = ex.ToString(); }
        }
    }
}
