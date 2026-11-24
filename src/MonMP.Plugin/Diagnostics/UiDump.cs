using System;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.Diagnostics
{
    // Development aid for the native multiplayer menus: with BepInEx/MonMP/ui-dump.request present,
    // writes the main menu's and the pause menu's canvas hierarchies (layout, text, fonts, sprites)
    // and a screenshot of each screen to BepInEx/MonMP/diagnostics/ui-*.
    internal static class UiDump
    {
        private static string Directory => Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics");
        private static readonly RequestFilePoll request = new RequestFilePoll(() => File.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "ui-dump.request")));
        internal static bool Requested => request.Check(Time.unscaledTime);
        internal static string[] Lines { get { try { return File.ReadAllLines(Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "ui-dump.request")).Select(l => l.Trim()).ToArray(); } catch (IOException) { return Array.Empty<string>(); } } }

        internal static void Write(string name, Transform root)
        {
            var text = new StringBuilder();
            Walk(root, 0, text);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "ui-" + name + ".txt"), text.ToString());
        }

        internal static void Screenshot(string name)
        {
            if (!Lines.Contains("offscreen"))
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(Directory, "ui-" + name + ".png"));
                return;
            }
            ScreenshotOffscreen(name);
        }

        // SW_HIDE leaves the window swap chain black on some drivers. For isolated UI captures,
        // render the actual active overlay canvases through a private camera instead. This fixture
        // remains in Development builds, requires an explicit request, and restores each lease.
        internal static void ScreenshotOffscreen(string name)
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>()
                .Where(canvas => canvas.isActiveAndEnabled && canvas.isRootCanvas && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                .Select(canvas => new CanvasLease(canvas)).ToArray();
            if (canvases.Length == 0) throw new InvalidOperationException("No active overlay canvas to capture.");
            int width = Screen.width, height = Screen.height;
            var previousTarget = RenderTexture.active;
            var cameraObject = new GameObject("MonMP UI capture camera") { hideFlags = HideFlags.HideAndDontSave, layer = 5 };
            RenderTexture? target = null;
            Texture2D? texture = null;
            try
            {
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
                target.Create();
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 5;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20;
                camera.targetTexture = target;
                foreach (var lease in canvases) lease.Attach(camera);
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = target;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(Path.Combine(Directory, "ui-" + name + ".png"), texture.EncodeToPNG());
            }
            finally
            {
                try
                {
                    foreach (var lease in canvases) lease.Restore();
                    Canvas.ForceUpdateCanvases();
                }
                finally
                {
                    RenderTexture.active = previousTarget;
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
            }
            File.WriteAllText(Path.Combine(Directory, "ui-" + name + "-render.txt"),
                "Native active overlay canvases rendered offscreen at " + width + "x" + height + ".\n" +
                "Restored canvas mode, camera, plane distance and root transform after capture.\n" +
                string.Join("\n", canvases.Select(lease => lease.Name)));
        }

        private sealed class CanvasLease
        {
            private readonly Canvas canvas;
            private readonly RenderMode mode;
            private readonly Camera camera;
            private readonly float distance;
            private readonly Vector2 anchorMin, anchorMax, pivot, size;
            private readonly Vector3 position, scale;
            private readonly Quaternion rotation;
            internal string Name => canvas.name;

            internal CanvasLease(Canvas canvas)
            {
                this.canvas = canvas;
                mode = canvas.renderMode; camera = canvas.worldCamera; distance = canvas.planeDistance;
                var root = (RectTransform)canvas.transform;
                anchorMin = root.anchorMin; anchorMax = root.anchorMax; pivot = root.pivot; size = root.sizeDelta;
                position = root.anchoredPosition3D; scale = root.localScale; rotation = root.localRotation;
            }

            internal void Attach(Camera capture)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = capture;
                canvas.planeDistance = 1;
            }

            internal void Restore()
            {
                if (canvas == null) return;
                canvas.renderMode = mode; canvas.worldCamera = camera; canvas.planeDistance = distance;
                var root = (RectTransform)canvas.transform;
                root.anchorMin = anchorMin; root.anchorMax = anchorMax; root.pivot = pivot; root.sizeDelta = size;
                root.anchoredPosition3D = position; root.localScale = scale; root.localRotation = rotation;
            }
        }

        private static void Walk(Transform t, int depth, StringBuilder text)
        {
            text.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name);
            if (t is RectTransform r)
                text.Append(" [a=").Append(r.anchorMin.ToString("F2")).Append("-").Append(r.anchorMax.ToString("F2"))
                    .Append(" p=").Append(r.anchoredPosition.ToString("F0")).Append(" s=").Append(r.sizeDelta.ToString("F0"))
                    .Append(" piv=").Append(r.pivot.ToString("F1")).Append("]");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                text.Append(" {").Append(c.GetType().Name);
                switch (c)
                {
                    case TMP_Text tmp:
                        text.Append(" \"").Append(tmp.text.Replace("\n", "\\n")).Append("\" font=").Append(tmp.font != null ? tmp.font.name : "-")
                            .Append(" size=").Append(tmp.fontSize.ToString("F0")).Append(" color=").Append(ColorUtility.ToHtmlStringRGBA(tmp.color))
                            .Append(" align=").Append(tmp.alignment).Append(" chars=").Append(tmp.textInfo?.characterCount ?? -1)
                            .Append(" cull=").Append(tmp.canvasRenderer.cull).Append(" alpha=").Append(tmp.canvasRenderer.GetAlpha().ToString("F2"))
                            .Append(" scale=").Append(tmp.transform.lossyScale.x.ToString("F3"));
                        break;
                    case Text legacy:
                        text.Append(" \"").Append(legacy.text).Append("\" font=").Append(legacy.font != null ? legacy.font.name : "-").Append(" size=").Append(legacy.fontSize);
                        break;
                    case Image image:
                        text.Append(" sprite=").Append(image.sprite != null ? image.sprite.name : "-").Append(" color=").Append(ColorUtility.ToHtmlStringRGBA(image.color))
                            .Append(" type=").Append(image.type).Append(" ppu=").Append(image.pixelsPerUnitMultiplier.ToString("F1"));
                        break;
                    case Canvas canvas:
                        text.Append(" mode=").Append(canvas.renderMode).Append(" order=").Append(canvas.sortingOrder);
                        break;
                    case CanvasScaler scaler:
                        text.Append(" ref=").Append(scaler.referenceResolution.ToString("F0")).Append(" match=").Append(scaler.matchWidthOrHeight);
                        break;
                    case LayoutGroup layout:
                        text.Append(" pad=").Append(layout.padding).Append(layout is HorizontalOrVerticalLayoutGroup hv ? " spacing=" + hv.spacing : "");
                        break;
                    case Button button:
                        text.Append(" listeners=").Append(button.onClick.GetPersistentEventCount());
                        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                            text.Append(" ").Append(button.onClick.GetPersistentTarget(i)?.GetType().Name).Append(".").Append(button.onClick.GetPersistentMethodName(i));
                        break;
                }
                text.Append("}");
            }
            text.Append('\n');
            foreach (Transform child in t) Walk(child, depth + 1, text);
        }
    }
}
