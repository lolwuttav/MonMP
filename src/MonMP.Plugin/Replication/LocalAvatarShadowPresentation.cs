#nullable enable
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    internal enum LocalAvatarView
    {
        Disabled,
        ShadowOnly,
        Visible
    }

    internal static class LocalAvatarPresentationPolicy
    {
        internal const float MinimumVisibleCameraClearance = 1.25f;

        internal static LocalAvatarView Select(bool multiplayerSession, bool thirdPersonCamera) =>
            Select(multiplayerSession, thirdPersonCamera, 0f, 0f);

        // These partial measurements fail closed: callers without both head and feet clearance
        // cannot prove that an alternate cabin/hood camera is outside the local body.
        internal static LocalAvatarView Select(bool multiplayerSession, bool vehicleCamera, float cameraDistanceFromHead) =>
            Select(multiplayerSession, vehicleCamera, cameraDistanceFromHead, 0f);

        // Requiring clearance from both the head and the feet/root avoids showing the duplicate
        // full body when an alternate cabin/hood camera sits beside the torso or legs.
        internal static LocalAvatarView Select(bool multiplayerSession, bool vehicleCamera,
            float cameraDistanceFromHead, float cameraDistanceFromFeet) =>
            !multiplayerSession ? LocalAvatarView.Disabled :
            vehicleCamera && IsClear(cameraDistanceFromHead) && IsClear(cameraDistanceFromFeet) ?
                LocalAvatarView.Visible : LocalAvatarView.ShadowOnly;

        private static bool IsClear(float distance) => !float.IsNaN(distance) && !float.IsInfinity(distance) &&
            distance >= MinimumVisibleCameraClearance;
    }

    internal static class AvatarHeadMeshPolicy
    {
        internal const float MinimumHeadBoneWeight = 0.65f;

        internal static bool IsHeadTriangle(float firstWeight, float secondWeight, float thirdWeight) =>
            firstWeight >= MinimumHeadBoneWeight && secondWeight >= MinimumHeadBoneWeight && thirdWeight >= MinimumHeadBoneWeight;
    }

    // A spectator avatar keeps only renderers attached to its head (and hair) visible. Capture the
    // original enabled state so ordinary poses restore assets that were disabled in the template.
    internal sealed class AvatarHeadOnlyPresentation
    {
        private readonly Renderer[] renderers;
        private readonly bool[] originalEnabled;
        private readonly bool[] headRenderers;
        private bool headOnly;

        internal AvatarHeadOnlyPresentation(Renderer[] renderers, bool[] headRenderers)
        {
            this.renderers = renderers ?? System.Array.Empty<Renderer>();
            this.headRenderers = headRenderers ?? System.Array.Empty<bool>();
            originalEnabled = new bool[this.renderers.Length];
            for (int i = 0; i < this.renderers.Length; i++)
                originalEnabled[i] = this.renderers[i] != null && this.renderers[i].enabled;
        }

        internal void SetHeadOnly(bool enabled)
        {
            if (headOnly == enabled) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer != null)
                    renderer.enabled = enabled ? i < headRenderers.Length && headRenderers[i] : originalEnabled[i];
            }
            headOnly = enabled;
        }

        internal void Restore() => SetHeadOnly(false);

        internal bool IsHeadRenderer(Renderer target)
        {
            for (int i = 0; i < renderers.Length && i < headRenderers.Length; i++)
                if (renderers[i] == target) return headRenderers[i];
            return false;
        }
    }

    // A local self-view uses the same posed stock body as remote players. In first person its
    // geometry still needs to reach the shadow pass, but must not draw a second body over the
    // native hands and held item. Keep each renderer's original mode so third person restores it.
    internal sealed class AvatarShadowCasting
    {
        private const string DepthExcludedRenderType = "MonMPShadowOnlyDepthExcluded";
        // Unity's built-in camera depth and DepthNormals textures contain only opaque queues
        // (<= 2500). ShadowsOnly renderers still have a ShadowCaster pass, so move only our
        // private material copies just past that cutoff while the body is in the local self-view.
        private const int DepthTextureOpaqueQueueLast = 2500;
        private readonly Renderer[] renderers;
        private readonly ShadowCastingMode[] originalModes;
        private readonly Material[][] originalMaterials;
        private Material[][]? shadowMaterials;
        private bool shadowOnly;

        internal bool IsShadowOnly => shadowOnly;

        internal AvatarShadowCasting(Renderer[] renderers)
        {
            this.renderers = renderers ?? System.Array.Empty<Renderer>();
            originalModes = new ShadowCastingMode[this.renderers.Length];
            originalMaterials = new Material[this.renderers.Length][];
            for (int i = 0; i < this.renderers.Length; i++)
            {
                var renderer = this.renderers[i];
                originalModes[i] = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.Off;
                originalMaterials[i] = renderer != null ? renderer.sharedMaterials : System.Array.Empty<Material>();
            }
        }

        internal void SetShadowOnly(bool enabled)
        {
            if (shadowOnly == enabled) return;

            if (!enabled)
            {
                RestoreOriginalPresentation();
                shadowOnly = false;
                return;
            }

            var copies = CreateDepthExcludedMaterials();
            try
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null) continue;
                    renderer.sharedMaterials = copies[i];
                    renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }
                shadowMaterials = copies;
                shadowOnly = true;
            }
            catch
            {
                RestoreOriginalPresentation();
                DestroyMaterials(copies);
                throw;
            }
        }

        internal void Restore() => SetShadowOnly(false);

        internal void RefreshMaterials(Renderer target)
        {
            if (shadowOnly)
                throw new System.InvalidOperationException("Restore avatar presentation before refreshing materials.");
            if (target == null) return;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] == target) originalMaterials[i] = target.sharedMaterials;
        }

        private Material[][] CreateDepthExcludedMaterials()
        {
            var copies = new Material[renderers.Length][];
            try
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var source = originalMaterials[i];
                    var target = copies[i] = new Material[source.Length];
                    for (int materialIndex = 0; materialIndex < source.Length; materialIndex++)
                    {
                        var original = source[materialIndex];
                        if (original == null) continue;
                        var copy = target[materialIndex] = new Material(original);
                        copy.SetOverrideTag("RenderType", DepthExcludedRenderType);
                        copy.renderQueue = DepthTextureOpaqueQueueLast + 1;
                    }
                }
                return copies;
            }
            catch
            {
                DestroyMaterials(copies);
                throw;
            }
        }

        private void RestoreOriginalPresentation()
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                renderer.sharedMaterials = originalMaterials[i];
                renderer.shadowCastingMode = originalModes[i];
            }
            DestroyMaterials(shadowMaterials);
            shadowMaterials = null;
        }

        private static void DestroyMaterials(Material[][]? materials)
        {
            if (materials == null) return;
            foreach (var set in materials)
            {
                if (set == null) continue;
                foreach (var material in set)
                    if (material != null) UnityEngine.Object.Destroy(material);
            }
        }
    }
}
