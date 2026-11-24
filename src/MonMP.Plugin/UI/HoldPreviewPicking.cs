using System;
using System.Collections.Generic;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;

namespace MonMP.Plugin.UI
{
    // Reads only the renderer-only item copies. It never creates colliders or native
    // gameplay components, and Clear releases references before their owner destroys them.
    internal sealed class HoldPreviewPicking
    {
        private sealed class MeshData
        {
            internal Bounds Bounds;
            internal NumericsVector3[]? Vertices;
            internal int[] Triangles = Array.Empty<int>();
        }

        private sealed class Target
        {
            internal MeshFilter Filter = null!;
            internal MeshRenderer Renderer = null!;
            internal MeshData Data = null!;
        }

        private readonly List<Target> targets = new List<Target>();

        // Static meshes and skins baked by the preview owner both arrive as MeshFilters.
        // Cached CPU data belongs to this item lifetime; call again after changing its mesh.
        internal void Rebuild(GameObject item)
        {
            Clear();
            if (item == null) return;
            var meshes = new Dictionary<int, MeshData>();
            foreach (var filter in item.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null) continue;
                int key = mesh.GetInstanceID();
                if (!meshes.TryGetValue(key, out var data)) { data = ReadMesh(mesh); meshes.Add(key, data); }
                targets.Add(new Target { Filter = filter, Renderer = renderer, Data = data });
            }
        }

        internal void Clear() => targets.Clear();

        internal bool TryPick(Ray ray, out Vector3 worldPoint, out Vector3 worldNormal, out bool approximate)
        {
            worldPoint = default; worldNormal = default; approximate = false;
            var direction = ToNumerics(ray.direction);
            if (!HoldPreviewRayMath.IsFinite(ToNumerics(ray.origin)) || !HoldPreviewRayMath.IsFinite(direction)) return false;
            double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
            if (length == 0) return false;
            var worldDirection = new Vector3((float)(direction.X / length), (float)(direction.Y / length), (float)(direction.Z / length));
            float nearest = float.PositiveInfinity;
            Vector3 nearestNormal = default;
            bool nearestApproximate = false;
            bool found = false;
            foreach (var target in targets)
            {
                if (target.Filter == null || target.Renderer == null || !target.Renderer.gameObject.activeInHierarchy ||
                    !target.Renderer.enabled || target.Renderer.forceRenderingOff) continue;
                var toLocal = target.Filter.transform.worldToLocalMatrix;
                var localOrigin = ToNumerics(toLocal.MultiplyPoint3x4(ray.origin));
                // Keep this direction unnormalized: the resulting parameter is world distance.
                var localDirection = ToNumerics(toLocal.MultiplyVector(worldDirection));
                var data = target.Data;
                if (!HoldPreviewRayMath.TryBounds(localOrigin, localDirection, ToNumerics(data.Bounds.min), ToNumerics(data.Bounds.max),
                    out float boundsDistance, out var boundsNormal)) continue;
                if (data.Vertices == null)
                {
                    Consider(boundsDistance, boundsNormal, true, toLocal);
                    continue;
                }
                var vertices = data.Vertices;
                for (int index = 0; index + 2 < data.Triangles.Length; index += 3)
                {
                    int a = data.Triangles[index], b = data.Triangles[index + 1], c = data.Triangles[index + 2];
                    if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length) continue;
                    if (HoldPreviewRayMath.TryTriangle(localOrigin, localDirection, vertices[a], vertices[b], vertices[c], out float distance, out var normal))
                        Consider(distance, normal, false, toLocal);
                }
            }
            if (found)
            {
                worldPoint = ray.origin + worldDirection * nearest;
                worldNormal = nearestNormal; approximate = nearestApproximate;
            }
            return found;

            void Consider(float distance, NumericsVector3 localNormal, bool boundsOnly, Matrix4x4 toLocal)
            {
                if (distance > nearest || (distance == nearest && (boundsOnly || !nearestApproximate))) return;
                // Inverse transpose keeps the normal perpendicular under nonuniform scale.
                var candidateNormal = toLocal.transpose.MultiplyVector(ToUnity(localNormal));
                var numericNormal = ToNumerics(candidateNormal);
                if (!HoldPreviewRayMath.IsFinite(numericNormal)) return;
                double normalLength = Math.Sqrt((double)numericNormal.X * numericNormal.X + (double)numericNormal.Y * numericNormal.Y + (double)numericNormal.Z * numericNormal.Z);
                if (normalLength == 0) return;
                candidateNormal = new Vector3((float)(numericNormal.X / normalLength), (float)(numericNormal.Y / normalLength), (float)(numericNormal.Z / normalLength));
                if (Vector3.Dot(candidateNormal, worldDirection) > 0) candidateNormal = -candidateNormal;
                nearest = distance; nearestNormal = candidateNormal; nearestApproximate = boundsOnly; found = true;
            }
        }

        private static MeshData ReadMesh(Mesh mesh)
        {
            var data = new MeshData { Bounds = mesh.bounds };
            // Accessing vertices/indices on imported non-readable meshes logs Unity errors.
            // Their local bounds remain usable, but must be reported as approximate.
            if (!mesh.isReadable) return data;
            try
            {
                var sourceVertices = mesh.vertices;
                var vertices = new NumericsVector3[sourceVertices.Length];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = ToNumerics(sourceVertices[i]);
                var triangles = new List<int>();
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    if (mesh.GetTopology(submesh) == MeshTopology.Triangles) triangles.AddRange(mesh.GetTriangles(submesh));
                data.Vertices = vertices; data.Triangles = triangles.ToArray();
            }
            catch (Exception)
            {
                // A mesh whose CPU data became unavailable still has the same explicit fallback.
                data.Vertices = null;
            }
            return data;
        }

        private static NumericsVector3 ToNumerics(Vector3 value) => new NumericsVector3(value.x, value.y, value.z);
        private static Vector3 ToUnity(NumericsVector3 value) => new Vector3(value.X, value.Y, value.Z);
    }
}
