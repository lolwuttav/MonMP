using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The fishing tube is a generated MeshRenderer, so copying its native mesh and
    // turning the entire mesh with a calibrated rod also turns the water endpoint.
    // Generate only the copied tube again, after the held visual has its final pose.
    // Native points, attachments, scripts and catch/lease state remain untouched.
    internal sealed class RemoteHeldLineVisual : IDisposable
    {
        private static readonly FieldInfo? LineField = AccessTools.Field(typeof(Tool_FishingRod), "Tube_FishingLine");
        private static readonly FieldInfo? AttachmentField = AccessTools.Field(typeof(Tool_FishingRod), "Transform_LineAttachment");
        private static readonly FieldInfo? CatchLocationField = AccessTools.Field(typeof(Tool_FishingRod), "Fish_Location");
        private Tool_FishingRod? rod;
        private TubeRenderer? source, copy;
        private MeshRenderer? copyRenderer;
        private MeshFilter? copyFilter;
        private Transform? presentedRoot;
        private Transform? attachment, catchLocation;
        private Mesh? ownedMesh;
        private Vector3[] points = Array.Empty<Vector3>();

        internal void BindIfFishingLine(InteractableItems item, Renderer original, MeshRenderer copiedRenderer,
            MeshFilter copiedFilter, Transform root)
        {
            if (!(item is Tool_FishingRod fishingRod) || !(LineField?.GetValue(fishingRod) is TubeRenderer line) ||
                line == null || original.gameObject != line.gameObject) return;
            Reset();
            rod = fishingRod; source = line; copyRenderer = copiedRenderer; copyFilter = copiedFilter; presentedRoot = root;
            attachment = AttachmentField?.GetValue(fishingRod) as Transform;
            catchLocation = CatchLocationField?.GetValue(fishingRod) as Transform;
            // Awake creates only the tube's own mesh. Give it the source material first
            // so the native mesh generator does not create a fallback material.
            copiedRenderer.sharedMaterials = original.sharedMaterials;
            copy = copiedRenderer.gameObject.AddComponent<TubeRenderer>();
            ownedMesh = copy.mesh;
            copy.enabled = false; // ForceUpdate below is the only driver of this render-only component.
            copy.radius = line.radius;
            if (line.radiuses != null && line.radiuses.Length != 0) copy.radiuses = (float[])line.radiuses.Clone();
            copy.edgeCount = line.edgeCount;
            copy.normalMode = line.normalMode;
            copy.caps = line.caps;
            copy.invertMesh = line.invertMesh;
            copy.calculateTangents = line.calculateTangents;
            copy.uvRect = line.uvRect;
            copy.uvRectCap = line.uvRectCap;
            copy.uvRectCapEndMirrored = line.uvRectCapEndMirrored;
            copy.uvAntiStretching = line.uvAntiStretching;
            copy.forwardAngleOffset = line.forwardAngleOffset;
            if (line.twists != null && line.twists.Length != 0) copy.twists = (float[])line.twists.Clone();
            // Native TubeRenderer.colors treats an empty assignment as null; its
            // default empty array already represents the native uncolored tube.
            if (line.colors != null && line.colors.Length != 0) copy.colors = (Color32[])line.colors.Clone();
        }

        internal void Present(bool caught)
        {
            if (rod == null || source == null || copy == null || copyRenderer == null || copyFilter == null || presentedRoot == null) return;
            if (!source.gameObject.activeInHierarchy)
            {
                // Far avatars can defer their full node/surface sync. End a cast now
                // rather than leave its last copied tube visible until that next pose.
                // The next ordinary surface sync restores the native enabled state.
                copyRenderer.enabled = false;
                return;
            }
            if (!copy.gameObject.activeInHierarchy || !copyRenderer.enabled || copyRenderer.forceRenderingOff) return;
            var nativePoints = source.points;
            if (nativePoints == null || nativePoints.Length < 2)
            {
                copyRenderer.enabled = false;
                return;
            }
            if (points.Length != nativePoints.Length)
            {
                points = new Vector3[nativePoints.Length];
                copy.points = points;
            }
            var nativeRoot = rod.transform;
            for (int i = 0; i < nativePoints.Length; i++)
            {
                // Tool_FishingRod and RemoteFishing write rod-local tube points.
                // The tip follows the calibrated rod. A cast remains fixed at its
                // native world position; a caught fish belongs to the rod instead.
                Vector3 world;
                if (i == 0 && attachment != null)
                    world = presentedRoot.TransformPoint(nativeRoot.InverseTransformPoint(attachment.position));
                else if (caught && i == nativePoints.Length - 1 && catchLocation != null)
                    world = presentedRoot.TransformPoint(nativeRoot.InverseTransformPoint(catchLocation.position));
                else
                    world = i == 0 || caught ? presentedRoot.TransformPoint(nativePoints[i]) : nativeRoot.TransformPoint(nativePoints[i]);
                points[i] = copy.transform.InverseTransformPoint(world);
            }
            // The ordinary surface pass follows native mesh swaps; rebind our owned
            // mesh afterward, without writing to the source filter or shared mesh.
            copyFilter.sharedMesh = ownedMesh;
            copy.Redraw();
            copy.ForceUpdate();
        }

        internal void Reset()
        {
            var oldCopy = copy; var oldMesh = ownedMesh;
            rod = null; source = null; copy = null; ownedMesh = null;
            copyRenderer = null; copyFilter = null; presentedRoot = null; attachment = null; catchLocation = null;
            points = Array.Empty<Vector3>();
            try { if (oldCopy != null) UnityEngine.Object.Destroy(oldCopy); }
            finally { if (oldMesh != null) UnityEngine.Object.Destroy(oldMesh); }
        }

        public void Dispose() => Reset();
    }
}
