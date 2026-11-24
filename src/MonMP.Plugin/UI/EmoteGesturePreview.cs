using System;
using MonMP.Core.Input;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    // Renders an isolated, cosmetic copy of the stock human body for the emote wheel. It never
    // attaches to a local player and has no colliders, game behaviours, or world-facing camera.
    internal sealed class EmoteGesturePreview : IDisposable
    {
        private const int PreviewLayer = 5; // Unity's UI layer.
        private const int TextureWidth = 384, TextureHeight = 512;
        private const float RenderInterval = 1f / 30f;
        private const float IsolatedCoordinate = 8192f;
        private const float SpitDuration = 2.4f; // StockAvatarBody's native spit-pose window.
        private const int MaxCreateAttemptsPerScene = 3;

        private GameObject? previewRoot;
        private Camera? previewCamera;
        private Light? keyLight;
        private RenderTexture? texture;
        private StockAvatarBody? body;
        private EmoteWheelSlot slot;
        private Color shirtColor;
        private bool hasShirtColor, disposed, hasRendered;
        private float elapsed, poseStep, nextRenderAt, nextCreateAt;
        private int failedCreateAttempts, sceneHandle = int.MinValue;

        internal void Tick(EmoteWheelSlot selected, Color shirt, float deltaTime)
        {
            if (disposed) return;
            if (selected == EmoteWheelSlot.None)
            {
                Hide();
                return;
            }

            SyncScene();
            if (selected != slot)
            {
                slot = selected;
                elapsed = 0f;
            }
            else
            {
                // The wheel can receive a long frame after focus changes. Keep the pose clock
                // bounded so one delayed frame does not jump past every short emote.
                elapsed = Mathf.Min(elapsed + Mathf.Clamp(IsFinite(deltaTime) ? deltaTime : 0f, 0f, 0.1f), SpitDuration);
            }

            // The hierarchy can be destroyed by a scene transition before the next managed tick.
            // Unity objects use fake-null semantics, while StockAvatarBody itself is an ordinary
            // managed object, so detect a lost root explicitly and release its graph/material.
            if (body != null && (previewRoot == null || previewCamera == null || texture == null))
                TearDownVisuals();
            if (body == null && !TryCreate(shirt)) return;
            if (body == null || previewCamera == null || texture == null || previewRoot == null) return;
            if (!previewRoot.activeSelf) { previewRoot.SetActive(true); hasRendered = false; }
            if (!texture.IsCreated() && !texture.Create())
            {
                TearDownVisuals();
                failedCreateAttempts = MaxCreateAttemptsPerScene;
                nextCreateAt = float.PositiveInfinity;
                return;
            }

            if (!hasShirtColor || shirt != shirtColor)
            {
                body.ShirtMaterial.color = shirt;
                shirtColor = shirt;
                hasShirtColor = true;
            }

            // Only the rendered pose is ever seen, so it is posed at the render rate.
            poseStep = Mathf.Min(poseStep + Mathf.Clamp(IsFinite(deltaTime) ? deltaTime : 0f, 0f, 0.1f), 0.1f);
            float now = Time.unscaledTime;
            if (!hasRendered || now >= nextRenderAt)
            {
                var overlay = StockAvatarBody.Overlay.Look(0f);
                overlay.BodyYaw = EmoteGameplayPolicy.PreviewViewerFacingYaw;
                overlay.GestureTime = -1f;
                ApplySelection(ref overlay);
                body.Present(StockAvatarBody.Mode.Stand, 0f, 0f, 0f, overlay, poseStep);
                // Feet are deliberately not solved against world geometry in this isolated preview.
                body.Solve(default(StockAvatarBody.Goals), poseStep);
                poseStep = 0f;
                // The key light exists for this render alone, otherwise every world camera has to
                // consider one more directional light. The scene's own shadow-casting sun would
                // redo its shadow pass here too, for a body that receives none.
                float shadowDistance = QualitySettings.shadowDistance;
                if (keyLight != null) keyLight.enabled = true;
                try
                {
                    QualitySettings.shadowDistance = 0f;
                    previewCamera.Render();
                    hasRendered = true;
                    nextRenderAt = now + RenderInterval;
                }
                catch
                {
                    // A graphics-device change or a temporarily unavailable RT should be
                    // recoverable on a later wheel open, without retrying asset discovery each
                    // frame in the meantime.
                    TearDownVisuals();
                    failedCreateAttempts = MaxCreateAttemptsPerScene;
                    nextCreateAt = float.PositiveInfinity;
                }
                finally
                {
                    QualitySettings.shadowDistance = shadowDistance;
                    if (keyLight != null) keyLight.enabled = false;
                }
            }
        }

        internal void Draw(Rect rect)
        {
            if (texture == null || !texture.IsCreated() || rect.width <= 0f || rect.height <= 0f) return;
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
        }

        // Crossing the wheel's center or closing it only hides the copy: cloning and stripping the
        // stock body is a visible hitch, so it happens once per scene rather than on every hover.
        internal void Hide()
        {
            slot = EmoteWheelSlot.None;
            elapsed = 0f;
            poseStep = 0f;
            if (previewRoot != null && previewRoot.activeSelf) previewRoot.SetActive(false);
        }

        // Clear also releases the temporary Unity objects, for a session or scene that ended.
        internal void Clear()
        {
            slot = EmoteWheelSlot.None;
            elapsed = 0f;
            hasShirtColor = false;
            TearDownVisuals();
        }

        public void Dispose()
        {
            if (disposed) return;
            Clear();
            disposed = true;
        }

        private void SyncScene()
        {
            int currentHandle = SceneManager.GetActiveScene().handle;
            if (currentHandle == sceneHandle) return;
            sceneHandle = currentHandle;
            failedCreateAttempts = 0;
            nextCreateAt = 0f;
            slot = EmoteWheelSlot.None;
            elapsed = 0f;
            hasShirtColor = false;
            TearDownVisuals();
        }

        private bool TryCreate(Color shirt)
        {
            if (failedCreateAttempts >= MaxCreateAttemptsPerScene || Time.unscaledTime < nextCreateAt) return false;

            try
            {
                previewRoot = new GameObject("MonMP emote preview");
                previewRoot.layer = PreviewLayer;
                previewRoot.transform.position = new Vector3(IsolatedCoordinate, IsolatedCoordinate, IsolatedCoordinate);

                body = StockAvatarBody.TryCreate(previewRoot.transform, shirt, out _);
                if (body == null)
                {
                    TearDownVisuals();
                    ScheduleRetry();
                    return false;
                }
                foreach (Transform child in previewRoot.transform)
                    SetLayerRecursively(child, PreviewLayer);
                foreach (var renderer in previewRoot.GetComponentsInChildren<Renderer>(true))
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                var lightObject = new GameObject("MonMP emote preview light");
                lightObject.layer = PreviewLayer;
                lightObject.transform.SetParent(previewRoot.transform, false);
                lightObject.transform.localRotation = Quaternion.Euler(32f, -28f, 0f);
                keyLight = lightObject.AddComponent<Light>();
                keyLight.enabled = false;
                keyLight.type = LightType.Directional;
                keyLight.cullingMask = 1 << PreviewLayer;
                keyLight.intensity = 1f;
                keyLight.shadows = LightShadows.None;

                var cameraObject = new GameObject("MonMP emote preview camera");
                cameraObject.layer = PreviewLayer;
                cameraObject.transform.SetParent(previewRoot.transform, false);
                cameraObject.transform.localPosition = new Vector3(0.45f, 0.93f, -3.6f);
                var target = previewRoot.transform.position + new Vector3(0f, 0.9f, 0f);
                cameraObject.transform.rotation = Quaternion.LookRotation(target - cameraObject.transform.position, Vector3.up);

                previewCamera = cameraObject.AddComponent<Camera>();
                previewCamera.enabled = false;
                previewCamera.orthographic = true;
                previewCamera.orthographicSize = 1.08f;
                previewCamera.aspect = (float)TextureWidth / TextureHeight;
                previewCamera.nearClipPlane = 0.1f;
                previewCamera.farClipPlane = 8f;
                previewCamera.cullingMask = 1 << PreviewLayer;
                previewCamera.clearFlags = CameraClearFlags.SolidColor;
                previewCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                previewCamera.useOcclusionCulling = false;
                previewCamera.allowHDR = false;
                previewCamera.allowMSAA = false;

                texture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32)
                {
                    name = "MonMP emote preview texture",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                if (!texture.Create()) throw new InvalidOperationException("Could not create emote preview texture.");
                previewCamera.targetTexture = texture;

                hasShirtColor = true;
                shirtColor = shirt;
                elapsed = 0f;
                hasRendered = false;
                nextRenderAt = 0f;
                return true;
            }
            catch
            {
                TearDownVisuals();
                ScheduleRetry();
                return false;
            }
        }

        private void ScheduleRetry()
        {
            // Try once immediately, then only twice more in this loaded scene. This allows stock
            // avatar art to appear just after a scene transition without repeatedly scanning the
            // native scene hierarchy.
            failedCreateAttempts++;
            nextCreateAt = Time.unscaledTime + (failedCreateAttempts == 1 ? 1f : 3f);
        }

        private void ApplySelection(ref StockAvatarBody.Overlay overlay)
        {
            switch (slot)
            {
                case EmoteWheelSlot.Sorry:
                    overlay.Sorry = 1f;
                    break;
                case EmoteWheelSlot.Spit:
                    overlay.SpitTime = Mathf.Min(elapsed, SpitDuration);
                    break;
                case EmoteWheelSlot.Wave:
                    ApplyGesture(ref overlay, PlayerEmoteKind.Wave);
                    break;
                case EmoteWheelSlot.Point:
                    ApplyGesture(ref overlay, PlayerEmoteKind.Point);
                    break;
                case EmoteWheelSlot.ThumbsUp:
                    ApplyGesture(ref overlay, PlayerEmoteKind.ThumbsUp);
                    break;
            }
        }

        private void ApplyGesture(ref StockAvatarBody.Overlay overlay, PlayerEmoteKind kind)
        {
            overlay.Gesture = kind;
            overlay.GestureTime = Mathf.Min(elapsed, StockAvatarBody.GestureDuration(kind));
        }

        private void TearDownVisuals()
        {
            if (previewCamera != null)
            {
                previewCamera.enabled = false;
                previewCamera.targetTexture = null;
            }
            previewCamera = null;
            keyLight = null;

            if (body != null) body.Destroy();
            body = null;

            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }

            if (previewRoot != null) UnityEngine.Object.Destroy(previewRoot);
            previewRoot = null;
            hasRendered = false;
            nextRenderAt = 0f;
        }

        private static void SetLayerRecursively(Transform transform, int layer)
        {
            transform.gameObject.layer = layer;
            foreach (Transform child in transform) SetLayerRecursively(child, layer);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
