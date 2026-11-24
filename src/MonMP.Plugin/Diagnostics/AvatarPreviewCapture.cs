using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Renders only the selected remote avatar so automated tests can retain a visual artifact
    // without depending on the game window, desktop focus or current camera view.
    internal static class AvatarPreviewCapture
    {
        private const int Width = 512;
        private const int Height = 512;
        private const int BitsPerPixel = 24;

        internal enum View { Front, Side, Back }

        // frame, when given, sets which way is front instead of the subject itself.
        internal static void SaveBitmap(Transform avatar, string path, View view = View.Front, Transform? frame = null,
            Vector3? focus = null, float distanceScale = 1f, Color? clearColor = null, bool png = false)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A preview path is required.", nameof(path));
            if (float.IsNaN(distanceScale) || float.IsInfinity(distanceScale) || distanceScale < .2f || distanceScale > 2f)
                throw new ArgumentOutOfRangeException(nameof(distanceScale));

            var transforms = avatar.GetComponentsInChildren<Transform>(true);
            var originalLayers = new int[transforms.Length];
            int layer = FindUnusedLayer();
            for (int i = 0; i < transforms.Length; i++)
            {
                originalLayers[i] = transforms[i].gameObject.layer;
                transforms[i].gameObject.layer = layer;
            }

            GameObject? cameraObject = null;
            GameObject? lightObject = null;
            RenderTexture? target = null;
            Texture2D? image = null;
            RenderTexture? previousTarget = RenderTexture.active;
            // Skinned meshes are skinned once per frame before rendering, so a pose set earlier in
            // this frame would render stale; bake each one at its current bone pose instead.
            var baked = new List<(SkinnedMeshRenderer Skin, GameObject Copy, Mesh Mesh)>();
            try
            {
                var renderers = new List<Renderer>();
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(false))
                {
                    if (!renderer.enabled) continue;
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        var mesh = new Mesh();
                        skin.BakeMesh(mesh);
                        var copy = new GameObject("MonMP Avatar Preview Bake") { layer = layer };
                        copy.transform.SetPositionAndRotation(skin.transform.position, skin.transform.rotation);
                        copy.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var meshRenderer = copy.AddComponent<MeshRenderer>();
                        meshRenderer.sharedMaterials = skin.sharedMaterials;
                        skin.enabled = false;
                        baked.Add((skin, copy, mesh));
                        renderers.Add(meshRenderer);
                    }
                    // Smoke and vomit particles would stretch the framing.
                    else if (!(renderer is ParticleSystemRenderer)) renderers.Add(renderer);
                }
                var bounds = BoundsFor(renderers);
                cameraObject = new GameObject("MonMP Avatar Preview Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = clearColor ?? new Color(0.12f, 0.12f, 0.12f, 1f);
                camera.cullingMask = 1 << layer;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 20f;
                camera.fieldOfView = 36f;
                camera.aspect = 1f;
                var facing = frame != null ? frame : avatar;
                var center = focus ?? bounds.center;
                camera.transform.position = center + (view == View.Side ? facing.right * 3.2f + facing.forward * 0.5f + Vector3.up * 0.2f
                    : view == View.Back ? -facing.forward * 2.6f + facing.right * 0.9f + Vector3.up * 1.1f
                    : facing.forward * 3.2f + facing.right * 0.35f + Vector3.up * 0.2f) * distanceScale;
                camera.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, Vector3.up);

                lightObject = new GameObject("MonMP Avatar Preview Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = Color.white;
                light.intensity = 1.1f;
                light.shadows = LightShadows.None;
                light.cullingMask = 1 << layer;
                lightObject.transform.rotation = Quaternion.Euler(35f, -30f, 0f);

                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                image = new Texture2D(Width, Height, png ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                image.Apply(false, false);
                if (png) File.WriteAllBytes(path, image.EncodeToPNG());
                else WriteBitmap(path, image.GetPixels());
            }
            finally
            {
                RenderTexture.active = previousTarget;
                for (int i = 0; i < transforms.Length; i++)
                    if (transforms[i] != null) transforms[i].gameObject.layer = originalLayers[i];
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (lightObject != null) UnityEngine.Object.DestroyImmediate(lightObject);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (image != null) UnityEngine.Object.Destroy(image);
                foreach (var (skin, copy, mesh) in baked)
                {
                    if (skin != null) skin.enabled = true;
                    UnityEngine.Object.DestroyImmediate(copy);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }

        private static int FindUnusedLayer()
        {
            for (int layer = 31; layer >= 8; layer--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            throw new InvalidOperationException("No unused Unity layer is available for the isolated avatar capture.");
        }

        private static Bounds BoundsFor(List<Renderer> renderers)
        {
            if (renderers.Count == 0) throw new InvalidOperationException("Remote avatar has no visible renderers.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        // What a camera sees, at the preview size.
        internal static void SaveView(Camera camera, string path)
        {
            var previous = RenderTexture.active; var previousTarget = camera.targetTexture;
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target; camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); texture.Apply();
                WriteBitmap(path, texture.GetPixels());
            }
            finally
            {
                camera.targetTexture = previousTarget; RenderTexture.active = previous;
                UnityEngine.Object.Destroy(texture); target.Release(); UnityEngine.Object.Destroy(target);
            }
        }

        private static void WriteBitmap(string path, Color[] pixels)
        {
            int rowBytes = (Width * BitsPerPixel / 8 + 3) & ~3;
            int imageBytes = rowBytes * Height;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0x4D42); writer.Write(54 + imageBytes); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(Width); writer.Write(Height); writer.Write((ushort)1); writer.Write((ushort)BitsPerPixel);
            writer.Write(0); writer.Write(imageBytes); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
            int padding = rowBytes - Width * 3;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var pixel = pixels[y * Width + x];
                    writer.Write(ToByte(pixel.b)); writer.Write(ToByte(pixel.g)); writer.Write(ToByte(pixel.r));
                }
                for (int i = 0; i < padding; i++) writer.Write((byte)0);
            }
        }

        private static byte ToByte(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
