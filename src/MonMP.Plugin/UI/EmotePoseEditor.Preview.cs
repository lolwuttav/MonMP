using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.UI
{
    internal sealed partial class EmotePoseEditor
    {
        private const int PreviewLayer = 5;
        private GameObject? previewRoot;
        private Transform? previewRig, wrist;
        private Camera? previewCamera;
        private RenderTexture? previewTexture;
        private StockAvatarBody? previewBody;
        private FirstPersonEmoteGesture? firstPerson;
        private Light? previewLight;
        private readonly List<(Renderer Renderer, bool Enabled)> previewRenderers = new List<(Renderer, bool)>();
        private readonly List<Material> previewMaterials = new List<Material>();
        private string previewLens = "";
        private Vector3 cameraPivot;
        private float orbit = 0f, elevation = 5f, distance = 2.7f;
        private bool cameraFramed;
        internal GameObject? PreviewRoot => previewRoot;
        internal Transform? PreviewRig => previewRig;
        internal Camera? PreviewCamera => previewCamera;
        internal RenderTexture? PreviewTexture => previewTexture;
        internal StockAvatarBody? PreviewBody => previewBody;
        internal IReadOnlyList<Material> PreviewMaterials => previewMaterials;

        private void RebuildPreview()
        {
            DestroyPreview();
            try
            {
                previewRoot = new GameObject("MonMP Emote Calibration Preview");
                previewRoot.layer = PreviewLayer;
                // Nearby coordinates retain sub-millimetre IK precision. Only the private
                // synchronous camera render may enable this stage's renderers or lighting.
                previewRoot.SetActive(false);
                previewRoot.transform.position = new Vector3(0f, -32f, 0f);
                var cameraObject = new GameObject("MonMP emote editor camera");
                cameraObject.transform.SetParent(previewRoot.transform, false);
                previewCamera = cameraObject.AddComponent<Camera>();
                previewCamera.enabled = false;
                previewCamera.cullingMask = 1 << PreviewLayer;
                previewCamera.clearFlags = CameraClearFlags.SolidColor;
                previewCamera.backgroundColor = new Color(.09f, .11f, .14f, 1f);
                previewCamera.nearClipPlane = .01f; previewCamera.farClipPlane = 12f;
                previewCamera.useOcclusionCulling = false;
                previewCamera.allowHDR = false; previewCamera.allowMSAA = false;
                previewCamera.fieldOfView = 50f; previewCamera.aspect = 1f;
                if (view == EmotePoseOverrides.ViewKind.FirstPerson) CreateFirstPersonPreview();
                else CreateRemotePreview();
                var lightObject = new GameObject("MonMP emote editor light");
                lightObject.transform.SetParent(previewRoot.transform, false);
                lightObject.transform.localRotation = Quaternion.Euler(35f, -30f, 0f);
                previewLight = lightObject.AddComponent<Light>();
                previewLight.enabled = false;
                previewLight.type = LightType.Directional; previewLight.cullingMask = 1 << PreviewLayer;
                previewLight.intensity = 1.15f; previewLight.shadows = LightShadows.None;
                int height = Mathf.Clamp(Mathf.RoundToInt(768f / previewCamera.aspect), 192, 1024);
                previewTexture = new RenderTexture(768, height, 24, RenderTextureFormat.ARGB32)
                { name = "MonMP emote editor texture", filterMode = FilterMode.Bilinear, useMipMap = false, autoGenerateMips = false };
                if (!previewTexture.Create()) throw new InvalidOperationException("Could not create preview texture.");
                previewCamera.targetTexture = previewTexture;
                SetLayers(previewRoot.transform);
                foreach (var renderer in previewRoot.GetComponentsInChildren<Renderer>(true))
                {
                    previewRenderers.Add((renderer, renderer.enabled)); renderer.enabled = false;
                }
                previewRoot.SetActive(true);
                if (view == EmotePoseOverrides.ViewKind.Remote) FitRemote(false);
                message = "Isolated preview ready. Wrist offsets use " + (view == EmotePoseOverrides.ViewKind.FirstPerson ? "camera" : "body") + " axes in metres.";
                RenderPreview();
            }
            catch (Exception ex) { DestroyPreview(); message = "Preview unavailable: " + ex.Message; }
        }

        private void CreateFirstPersonPreview()
        {
            var owner = gameplay.Player.Get_VisualHands;
            var sourceCamera = gameplay.PlayerWalking?.playerCameraController?.cam3DInFrontOfEverything ?? gameplay.Player.mainCamera;
            if (owner == null || sourceCamera == null) throw new InvalidOperationException("Native visual hands / foreground camera are unavailable.");
            var source = owner.transform;
            var nodes = new Dictionary<Transform, Transform>();
            int nodeCount = 0;
            previewRig = CopyNode(source, previewRoot!.transform);
            var inverse = Quaternion.Inverse(sourceCamera.transform.rotation);
            previewRig.localPosition = inverse * (source.position - sourceCamera.transform.position);
            previewRig.localRotation = inverse * source.rotation;
            previewRig.localScale = source.lossyScale;
            previewRig.gameObject.SetActive(true);
            int copiedSkins = 0;
            foreach (var skin in owner.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.sharedMesh == null || !skin.enabled || !skin.gameObject.activeInHierarchy || !ArmBoundSkin(skin, source)) continue;
                var skinBones = skin.bones;
                if (skinBones.Length == 0 || skinBones.Any(b => b == null || !nodes.ContainsKey(b))) continue;
                var target = nodes[skin.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
                target.sharedMesh = skin.sharedMesh;
                target.bones = skinBones.Select(b => nodes[b]).ToArray();
                target.rootBone = skin.rootBone != null && nodes.TryGetValue(skin.rootBone, out var rootBone) ? rootBone : previewRig;
                target.localBounds = skin.localBounds; target.quality = skin.quality;
                target.updateWhenOffscreen = true; target.shadowCastingMode = ShadowCastingMode.Off; target.receiveShadows = false;
                var materials = skin.sharedMaterials;
                var copies = new Material[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    var material = new Material(materials[i]) { name = "MonMP emote preview " + materials[i].name };
                    previewMaterials.Add(material); copies[i] = material;
                }
                target.sharedMaterials = copies;
                var block = new MaterialPropertyBlock(); skin.GetPropertyBlock(block); target.SetPropertyBlock(block);
                for (int i = 0; i < materials.Length; i++) { skin.GetPropertyBlock(block, i); target.SetPropertyBlock(block, i); }
                for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) target.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                copiedSkins++;
            }
            if (copiedSkins == 0) throw new InvalidOperationException("No visible native arm-bound skins are available. Return to first-person and refresh.");
            float nativeFov = sourceCamera.fieldOfView;
            bool supportedLens = !float.IsNaN(nativeFov) && !float.IsInfinity(nativeFov) && nativeFov >= 40f && nativeFov <= 120f;
            previewCamera!.fieldOfView = supportedLens ? nativeFov : 60f;
            previewLens = supportedLens ? "Native foreground lens: " + nativeFov.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "°"
                : "Preview lens: 60° preset (native zoom is outside the supported gesture lens)";
            previewCamera.aspect = Mathf.Clamp(sourceCamera.aspect, .75f, 4f);
            previewCamera.transform.localPosition = Vector3.zero; previewCamera.transform.localRotation = Quaternion.identity;
            firstPerson = new FirstPersonEmoteGesture();
            if (!firstPerson.BeginPreview(previewRig, previewCamera, kind)) throw new InvalidOperationException("Native first-person hand rig does not match the supported chain.");
            wrist = previewRig.Find("Armature/Chest/Shoulder.R/Upper Arm.R/Lower Arm.R/Hand.R");

            Transform CopyNode(Transform native, Transform parent)
            {
                if (++nodeCount > 4096) throw new InvalidOperationException("Native hands hierarchy exceeds the preview limit.");
                var copy = new GameObject(native.name); copy.layer = PreviewLayer;
                copy.transform.SetParent(parent, false);
                copy.transform.localPosition = native.localPosition; copy.transform.localRotation = native.localRotation; copy.transform.localScale = native.localScale;
                copy.SetActive(native.gameObject.activeSelf); nodes.Add(native, copy.transform);
                foreach (Transform child in native) CopyNode(child, copy.transform);
                return copy.transform;
            }
        }

        private static bool ArmBoundSkin(SkinnedMeshRenderer skin, Transform owner)
        {
            // Tools may be children of the hands, with their own unrelated skinned rigs. Copy
            // only native arm/sleeve skins, never held-item meshes or gameplay components.
            for (var node = skin.transform; node != null && node != owner; node = node.parent)
                if (node.GetComponent<InteractableItems>() != null) return false;
            foreach (var bone in skin.bones)
                if (bone != null && bone.IsChildOf(owner) &&
                    (bone.name == "Upper Arm.R" || bone.name == "Upper Arm.L" || bone.name == "Hand.R" || bone.name == "Hand.L")) return true;
            return false;
        }

        private void CreateRemotePreview()
        {
            var rig = new GameObject("MonMP emote remote rig"); rig.transform.SetParent(previewRoot!.transform, false);
            previewRig = rig.transform;
            int shirt = gameplay.Player.Get_VisualHands != null ? gameplay.Player.Get_VisualHands.Get_ShirtColor : 0;
            previewBody = StockAvatarBody.TryCreate(previewRig, ShirtColor(shirt), out var reason);
            if (previewBody == null) throw new InvalidOperationException(reason);
            wrist = previewBody.RightHand;
        }

        private void RenderPreview()
        {
            if (!Visible || previewRoot == null || previewCamera == null || previewTexture == null) return;
            try
            {
                if (previewRig == null || wrist == null) throw new InvalidOperationException("Preview hierarchy was unloaded. Refresh the preview.");
                if (firstPerson != null) firstPerson.ApplyPreview(elapsed, draft);
                else if (previewBody != null)
                {
                    var overlay = StockAvatarBody.Overlay.Look(0f); overlay.Gesture = kind; overlay.GestureTime = elapsed;
                    previewBody.EmotePosePreview = draft;
                    previewBody.Present(previewStance, 0f, 0f, 0f, overlay, 1f);
                    previewBody.Solve(new StockAvatarBody.Goals { Feet = false }, 1f);
                    UpdateRemoteCamera();
                }
                if (!previewTexture.IsCreated() && !previewTexture.Create()) throw new InvalidOperationException("Preview texture was lost.");
                try
                {
                    foreach (var surface in previewRenderers)
                        if (surface.Renderer != null) surface.Renderer.enabled = surface.Enabled;
                    if (previewLight != null) previewLight.enabled = true;
                    previewCamera.Render();
                }
                finally
                {
                    foreach (var surface in previewRenderers)
                        if (surface.Renderer != null) surface.Renderer.enabled = false;
                    if (previewLight != null) previewLight.enabled = false;
                }
            }
            catch (Exception ex) { DestroyPreview(); message = "Preview stopped: " + ex.Message; }
        }

        private void FitRemote(bool hand)
        {
            if (previewRoot == null || previewBody == null) return;
            cameraPivot = hand ? previewBody.RightHand.position : previewRoot.transform.position + Vector3.up * .9f;
            distance = hand ? .8f : 2.7f; cameraFramed = true; UpdateRemoteCamera();
        }
        private void UpdateRemoteCamera()
        {
            if (previewCamera == null || previewRoot == null) return;
            if (!cameraFramed) { cameraPivot = previewRoot.transform.position + Vector3.up * .9f; cameraFramed = true; }
            var direction = Quaternion.Euler(-elevation, orbit, 0f) * Vector3.forward;
            previewCamera.transform.position = cameraPivot + direction * distance;
            previewCamera.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
        }
        private Rect ImageRect(Rect viewport)
        {
            if (previewTexture == null) return viewport;
            float aspect = (float)previewTexture.width / previewTexture.height;
            if (viewport.width / viewport.height > aspect)
            { float width = viewport.height * aspect; return new Rect(viewport.x + (viewport.width - width) * .5f, viewport.y, width, viewport.height); }
            float height = viewport.width / aspect;
            return new Rect(viewport.x, viewport.y + (viewport.height - height) * .5f, viewport.width, height);
        }
        private static void SetLayers(Transform root)
        { root.gameObject.layer = PreviewLayer; foreach (Transform child in root) SetLayers(child); }
        private void DestroyPreview()
        {
            firstPerson?.Cancel(); firstPerson = null;
            if (previewCamera != null) { previewCamera.enabled = false; previewCamera.targetTexture = null; }
            previewBody?.Destroy(); previewBody = null;
            if (previewTexture != null) { previewTexture.Release(); UnityEngine.Object.Destroy(previewTexture); }
            previewTexture = null;
            if (previewLight != null) previewLight.enabled = false;
            foreach (var surface in previewRenderers) if (surface.Renderer != null) surface.Renderer.enabled = false;
            previewRenderers.Clear(); previewLight = null; previewLens = "";
            foreach (var material in previewMaterials) if (material != null) UnityEngine.Object.Destroy(material);
            previewMaterials.Clear();
            if (previewRoot != null) { previewRoot.SetActive(false); UnityEngine.Object.Destroy(previewRoot); }
            previewRoot = null; previewRig = null; previewCamera = null; wrist = null; cameraFramed = false;
        }
        private static Color ShirtColor(int shirt)
        {
            switch (shirt)
            {
                case 1: return new Color(.09803923f, .4170849f, .8862745f);
                case 2: return new Color(.09803923f, .8862745f, .4617344f);
                case 3: return new Color(.6320754f, .6320754f, .6320754f);
                case 4: return new Color(1f, .5571218f, .08176094f);
                case 5: return new Color(1f, .5345911f, .9706057f);
                case 6: return new Color(.9756485f, .9811321f, .4319449f);
                case 7: return new Color(.47f, .31f, .66f);
                case 8: return Color.white;
                default: return new Color(.8862745f, .09803923f, .2392157f);
            }
        }
    }
}
