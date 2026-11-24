using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Compact low-poly field glasses in the game's muted Standard-material style.
    // Meshes/materials are owned by this visual; no colliders, scripts or gameplay items.
    internal sealed class RemoteBinoculars : IDisposable
    {
        internal const float EyeHeight = 0.05f;
        internal readonly GameObject Object;
        private readonly Mesh tube, block;
        private readonly Material rubber, metal, glass;

        internal RemoteBinoculars(Transform parent)
        {
            var shader = Shader.Find("Standard") ?? throw new InvalidOperationException("Binocular material shader unavailable.");
            Object = new GameObject("MonMP Binoculars");
            Object.transform.SetParent(parent, false);
            Object.SetActive(false);
            rubber = new Material(shader) { color = new Color(0.10f, 0.13f, 0.105f) };
            metal = new Material(shader) { color = new Color(0.12f, 0.13f, 0.14f) };
            glass = new Material(shader) { color = new Color(0.075f, 0.18f, 0.23f) };
            rubber.SetFloat("_Glossiness", 0.12f); metal.SetFloat("_Glossiness", 0.3f);
            glass.SetFloat("_Glossiness", 0.8f); glass.SetFloat("_Metallic", 0.35f);
            tube = Frustum(); block = Box();
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * 0.042f;
                Part("Barrel", tube, rubber, new Vector3(x, 0, 0.065f), new Vector3(0.041f, 0.041f, 0.13f));
                Part("Objective rim", tube, metal, new Vector3(x, 0, 0.135f), new Vector3(0.044f, 0.044f, 0.02f));
                Part("Objective glass", tube, glass, new Vector3(x, 0, 0.146f), new Vector3(0.037f, 0.037f, 0.002f));
                Part("Eyecup", tube, metal, new Vector3(x, 0, -0.019f), new Vector3(0.028f, 0.028f, 0.04f));
            }
            Part("Bridge", block, rubber, new Vector3(0, 0.004f, 0.035f), new Vector3(0.084f, 0.035f, 0.04f));
            Part("Focus wheel", tube, metal, new Vector3(0, 0.026f, 0.025f), new Vector3(0.016f, 0.016f, 0.028f));
        }

        internal void Show(bool visible) { if (Object != null && Object.activeSelf != visible) Object.SetActive(visible); }
        internal void Place(StockAvatarBody body)
        {
            float scale = body.VisualScale;
            Object.transform.SetPositionAndRotation(body.BinocularEyePosition, body.Aim);
            Vector3 parentScale = Object.transform.parent.lossyScale;
            Object.transform.localScale = new Vector3(scale / parentScale.x, scale / parentScale.y, scale / parentScale.z);
            Show(true);
        }
        private void Part(string name, Mesh mesh, Material material, Vector3 position, Vector3 scale)
        {
            var part = new GameObject(name); part.layer = Object.transform.parent.gameObject.layer;
            part.transform.SetParent(Object.transform, false); part.transform.localPosition = position; part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
        private static Mesh Frustum()
        {
            const int sides = 12;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2 / sides, b = (i + 1) * Mathf.PI * 2 / sides;
                var rearA = new Vector3(Mathf.Cos(a) * 0.8f, Mathf.Sin(a) * 0.8f, -0.5f);
                var rearB = new Vector3(Mathf.Cos(b) * 0.8f, Mathf.Sin(b) * 0.8f, -0.5f);
                var frontA = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0.5f);
                var frontB = new Vector3(Mathf.Cos(b), Mathf.Sin(b), 0.5f);
                int start = vertices.Count;
                vertices.AddRange(new[] { rearA, rearB, frontA, frontB, new Vector3(0, 0, -0.5f), new Vector3(0, 0, 0.5f) });
                triangles.AddRange(new[] { start, start + 1, start + 2, start + 2, start + 1, start + 3,
                    start + 4, start + 1, start, start + 5, start + 2, start + 3 });
            }
            var mesh = new Mesh { name = "MonMP Field Glasses Barrel" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }
        private static Mesh Box()
        {
            var mesh = new Mesh { name = "MonMP Field Glasses Bridge" };
            mesh.vertices = new[] { new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f) };
            mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        public void Dispose()
        {
            UnityEngine.Object.Destroy(Object); UnityEngine.Object.Destroy(tube); UnityEngine.Object.Destroy(block);
            UnityEngine.Object.Destroy(rubber); UnityEngine.Object.Destroy(metal); UnityEngine.Object.Destroy(glass);
        }
    }
}
