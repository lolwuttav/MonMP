using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class NativeShirtCloseCapture
    {
        internal static void Save(Transform avatar, SkinnedMeshRenderer shirt, string directory, string prefix)
        {
            var baked = new Mesh(); var copy = new GameObject("MonMP Shirt Close Capture");
            var cameraObject = new GameObject("MonMP Shirt Close Camera"); var lightObject = new GameObject("MonMP Shirt Close Light");
            var target = new RenderTexture(640, 640, 24, RenderTextureFormat.ARGB32); var image = new Texture2D(640, 640, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                int layer = Enumerable.Range(8, 24).Reverse().First(i => string.IsNullOrEmpty(LayerMask.LayerToName(i)));
                copy.layer = layer; shirt.BakeMesh(baked);
                copy.transform.SetPositionAndRotation(shirt.transform.position, shirt.transform.rotation);
                copy.AddComponent<MeshFilter>().sharedMesh = baked;
                var renderer = copy.AddComponent<MeshRenderer>(); renderer.sharedMaterial = shirt.sharedMaterial;
                var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
                camera.cullingMask = 1 << layer; camera.nearClipPlane = 0.01f; camera.farClipPlane = 10; camera.orthographic = true; camera.aspect = 1;
                target.Create(); camera.targetTexture = target;
                var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.1f; light.shadows = LightShadows.None; light.cullingMask = 1 << layer;
                var bounds = renderer.bounds;
                foreach (string view in new[] { "front", "back", "left", "right", "cuff-left", "cuff-right" })
                {
                    bool left = view.Contains("left"), cuff = view.StartsWith("cuff", StringComparison.Ordinal);
                    Vector3 center = bounds.center;
                    camera.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.12f;
                    Vector3 direction = view == "front" ? avatar.forward : view == "back" ? -avatar.forward : left ? -avatar.right : avatar.right;
                    if (cuff)
                    {
                        string suffix = left ? ".L" : ".R";
                        var bones = avatar.GetComponentsInChildren<Transform>(true);
                        var hand = bones.First(b => b.name == "Hand" + suffix);
                        var lower = bones.First(b => b.name == "Lower Arm" + suffix);
                        center = Vector3.Lerp(lower.position, hand.position, 0.72f); camera.orthographicSize = 0.22f;
                    }
                    camera.transform.position = center + direction * 2f + avatar.forward * (view == "front" || view == "back" ? 0 : 0.1f);
                    camera.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, Vector3.up);
                    lightObject.transform.rotation = camera.transform.rotation * Quaternion.Euler(20, -25, 0);
                    camera.Render(); RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); image.Apply();
                    File.WriteAllBytes(Path.Combine(directory, prefix + "-cloth-" + view + ".png"), image.EncodeToPNG());
                }
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(copy); UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(lightObject);
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(image); UnityEngine.Object.Destroy(baked);
            }
        }
    }
}
