using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // One presentation copy per remote avatar. The native item retains its exact transform,
    // collider, Rigidbody, scripts and authoritative world/network state. Particle-only
    // children may follow the final visual pose reversibly; gameplay transforms stay native.
    // forceRenderingOff suppresses only meshes, preserving native renderer enabled flags.
    internal sealed class RemoteHeldVisual : IDisposable
    {
        private sealed class Node
        {
            internal Transform Source = null!, Copy = null!;
            internal GameObject SourceObject = null!, CopyObject = null!;
            internal bool Root;
        }
        private sealed class Surface
        {
            internal Renderer Source = null!;
            internal MeshRenderer Copy = null!;
            internal bool ForceWasOff;
            internal RendererSuppressionScope? Suppression;
            internal Mesh? Baked;
            internal MeshFilter? SourceMesh;
            internal MeshFilter CopyMesh = null!;
            internal readonly List<Material> Materials = new List<Material>();
            internal readonly List<Material> MaterialScratch = new List<Material>();
        }
        private readonly HeldGripOverrides overrides;
        private readonly RemoteHeldLineVisual line = new RemoteHeldLineVisual();
        private readonly RemoteHeldParticleVisual particles = new RemoteHeldParticleVisual();
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<Surface> surfaces = new List<Surface>();
        private InteractableItems? source;
        private GameObject? sourceObject;
        private bool suppressSource;
        private GameObject? visual;
        private RemoteHeldVisual? catchVisual;
        private readonly HashSet<int> warnedSources = new HashSet<int>();
        internal Transform? PresentedTransform => visual != null ? visual.transform : null;
        internal RemoteHeldVisual(HeldGripOverrides overrides) { this.overrides = overrides; }
        // Local speculative pickup: renderer copies only, with native first-person
        // placement supplied by the caller. No equip/use/Grab or effect callbacks.
        internal bool TryPresentLocal(InteractableItems item, Vector3 position, Quaternion rotation,
            Vector3 scale, Transform view, bool equipped)
        {
            if (item == null || NativeOwns(item)) { Reset(); return false; }
            if (!TryPresentObject(item.gameObject, position, rotation, scale, true, view, true)) return false;
            visual!.transform.localScale = RelativeScale(scale, view.lossyScale);
            int layer = equipped ? LayerMask.NameToLayer("3DObjectInFrontOfEverything") : -1;
            if (layer >= 0)
            {
                foreach (var node in nodes) if (node.CopyObject != null) node.CopyObject.layer = layer;
                foreach (var surface in surfaces) if (surface.Copy != null) surface.Copy.gameObject.layer = layer;
            }
            return true;
        }
        internal bool TryPresent(InteractableItems item, string shape, Quaternion bodyRotation, out Vector3 position, out Quaternion rotation,
            Vector3? equippedPosition = null, Quaternion? equippedRotation = null, Transform? avatarFrame = null)
        {
            position = default; rotation = default;
            if (item == null || NativeOwns(item)) { Reset(); return false; }
            // Even a zero-offset or uncalibrated item needs the same render frame as
            // its holder. Only this mesh copy follows/clamps; native physics stays put.
            var offset = Vector3.zero; var turn = Quaternion.identity;
            overrides.TryGetVisualOffset(item, shape, out offset, out turn);
            position = (equippedPosition ?? item.transform.position) + bodyRotation * offset;
            rotation = (equippedRotation ?? item.transform.rotation) * turn;
            if (TryPresentObject(item.gameObject, position, rotation, item.transform.lossyScale, true, avatarFrame)) return true;
            position = default; rotation = default; return false;
        }

        // Catch copies use the rod's already calibrated frame, without applying the
        // result item's independent held-tool calibration or running gameplay components.
        private bool TryPresentObject(GameObject item, Vector3 position, Quaternion rotation, Vector3 scale,
            bool hideSource, Transform? avatarFrame = null, bool localPreview = false)
        {
            try
            {
                particles.Restore();
                if (sourceObject != item || visual == null || suppressSource != hideSource)
                {
                    Reset(); sourceObject = item; source = item.GetComponent<InteractableItems>(); suppressSource = hideSource;
                    visual = new GameObject("MonMP Remote Held Presentation");
                    CopyTree(item.transform, visual.transform, true);
                    if (surfaces.Count == 0) { Reset(); return false; }
                }
                if (visual!.transform.parent != avatarFrame) visual.transform.SetParent(avatarFrame, true);
                visual!.transform.SetPositionAndRotation(position, rotation);
                visual.transform.localScale = scale;
                foreach (var node in nodes)
                {
                    if (node.Copy == null) continue;
                    if (node.Source == null || !node.Root && !node.Source.IsChildOf(item.transform))
                    { if (node.CopyObject.activeSelf) node.CopyObject.SetActive(false); continue; }
                    if (!node.Root)
                    {
                        node.Copy.localPosition = node.Source.localPosition;
                        node.Copy.localRotation = node.Source.localRotation;
                        node.Copy.localScale = node.Source.localScale;
                    }
                    int layer = node.SourceObject.layer;
                    if (node.CopyObject.layer != layer) node.CopyObject.layer = layer;
                    bool active = node.Root ? !hideSource || node.SourceObject.activeInHierarchy : node.SourceObject.activeSelf;
                    if (node.CopyObject.activeSelf != active) node.CopyObject.SetActive(active);
                }
                foreach (var surface in surfaces)
                {
                    if (surface.Copy == null) continue;
                    if (surface.Source == null) { surface.Copy.enabled = false; continue; }
                    surface.Copy.gameObject.layer = surface.Source.gameObject.layer;
                    surface.Copy.enabled = surface.Source.enabled;
                    surface.Copy.forceRenderingOff = surface.ForceWasOff;
                    surface.MaterialScratch.Clear();
                    // A pending local pickup must not carry the native blue hover material.
                    if (localPreview && source != null &&
                        source.meshRenderer == surface.Source && source.OriginalMaterials?.Length > 0)
                        surface.MaterialScratch.AddRange(source.OriginalMaterials);
                    else surface.Source.GetSharedMaterials(surface.MaterialScratch);
                    bool materialsChanged = surface.MaterialScratch.Count != surface.Materials.Count;
                    if (!materialsChanged)
                        for (int i = 0; i < surface.Materials.Count; i++)
                            if (surface.MaterialScratch[i] != surface.Materials[i]) { materialsChanged = true; break; }
                    if (materialsChanged)
                    {
                        surface.Copy.sharedMaterials = surface.MaterialScratch.ToArray();
                        surface.Materials.Clear(); surface.Materials.AddRange(surface.MaterialScratch);
                    }
                    surface.Copy.shadowCastingMode = surface.Source.shadowCastingMode;
                    surface.Copy.receiveShadows = surface.Source.receiveShadows;
                    if (surface.Baked != null && surface.Source is SkinnedMeshRenderer skin)
                    {
                        // The copy retains renderer/ancestor scale; compensate it in the baked vertices.
                        skin.BakeMesh(surface.Baked, true);
                    }
                    else if (surface.Source is MeshRenderer)
                    {
                        // Reacquire if native code replaces a filter, while retaining mesh swaps.
                        if (surface.SourceMesh == null) surface.SourceMesh = surface.Source.GetComponent<MeshFilter>();
                        if (surface.SourceMesh != null) surface.CopyMesh.sharedMesh = surface.SourceMesh.sharedMesh;
                    }
                    if (suppressSource) surface.Source.forceRenderingOff = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                Reset();
                if (warnedSources.Add(item.GetInstanceID())) Debug.LogWarning("MonMP kept native held-item rendering for " + item.name + ": " + ex.Message);
                return false;
            }
        }
        internal void MoveWithHands(Vector3 translation)
        {
            if (visual == null || source == null) return;
            // Far avatars can defer TryPresent. A local take/equip must restore the
            // emitter and mesh immediately before this final pass writes any pose.
            if (NativeOwns(source)) { Reset(); return; }
            if (translation.sqrMagnitude > 0) visual.transform.position += translation;
            // Refresh after the body's final foot/reach correction, so tool effects,
            // the rod tip and fixed world hook all use the final presentation frame.
            try
            {
                line.Present(source is Tool_FishingRod rod && RemoteFishing.IsCaught(rod));
                particles.Present();
            }
            catch (Exception ex)
            {
                var failedSource = source;
                Reset();
                if (failedSource != null && warnedSources.Add(failedSource.GetInstanceID()))
                    Debug.LogWarning("MonMP kept native held-effect rendering: " + ex.Message);
            }
            // A late catch has its own lifecycle. A missing/destroyed result must never
            // reset the calibrated rod or restore its original uncalibrated mesh.
            if (visual != null && source is Tool_FishingRod fishingRod)
            {
                try { PresentCatch(fishingRod); }
                catch (Exception ex)
                {
                    catchVisual?.Reset();
                    if (warnedSources.Add(fishingRod.GetInstanceID()))
                        Debug.LogWarning("MonMP kept native fishing-catch rendering: " + ex.Message);
                }
            }
        }

        private void PresentCatch(Tool_FishingRod rod)
        {
            if (!RemoteFishing.TryGetCatchVisual(rod, out var location, out var output, out bool template) ||
                !template && output.GetComponent<InteractableItems>() is InteractableItems item && NativeOwns(item))
            { catchVisual?.Reset(); return; }
            var nativeRoot = rod.transform;
            var view = visual!.transform;
            // The catch hangs from the presented hook with gravity, whatever the rod's tilt,
            // and turns with the native Caught spin. Its offset from the hook is kept as is.
            Vector3 offset;
            Quaternion localRotation;
            Vector3 scale;
            var unrotate = Quaternion.Inverse(location.rotation);
            if (!template && output.transform.IsChildOf(location))
            {
                offset = unrotate * (output.transform.position - location.position);
                localRotation = unrotate * output.transform.rotation;
                scale = RelativeScale(output.transform.lossyScale, nativeRoot.lossyScale);
            }
            else
            {
                // Item replication deliberately detaches held replicas. Reconstruct the
                // native Fish_Location attachment for the visual, keeping physics untouched.
                Vector3 attach = output.GetComponent<Item_Fish>() is Item_Fish fish ?
                    new Vector3(0, 0, -(fish.Get_FishSizeReference * fish.Value1) * .5f) : Vector3.zero;
                offset = unrotate * (location.TransformPoint(attach) - location.position);
                localRotation = Quaternion.identity;
                scale = Vector3.Scale(RelativeScale(location.lossyScale, nativeRoot.lossyScale), output.transform.localScale);
            }
            var hook = view.TransformPoint(nativeRoot.InverseTransformPoint(location.position));
            var presented = view.rotation * Quaternion.Inverse(nativeRoot.rotation) * location.rotation;
            // Native catches sit along Fish_Location's -Z, the axis its spin turns about.
            var hanging = Quaternion.FromToRotation(presented * Vector3.back, Vector3.down) * presented;
            hanging = Quaternion.AngleAxis(RemoteFishing.CatchSpinDegrees(RemoteFishing.CaughtSeconds(rod)), Vector3.up) * hanging;
            float ratio = nativeRoot.lossyScale.x == 0 ? 1f : view.lossyScale.x / nativeRoot.lossyScale.x;
            catchVisual ??= new RemoteHeldVisual(overrides);
            catchVisual.TryPresentObject(output, hook + hanging * offset * ratio, hanging * localRotation,
                Vector3.Scale(view.lossyScale, scale), !template);
        }

        private static Vector3 RelativeScale(Vector3 value, Vector3 parent) => new Vector3(
            parent.x == 0 ? 0 : value.x / parent.x, parent.y == 0 ? 0 : value.y / parent.y, parent.z == 0 ? 0 : value.z / parent.z);
        private static bool NativeOwns(InteractableItems item)
        {
            if (item is InteractableTools tool && tool.isEquiped) return true;
            var player = MonMPPlugin.Instance?.Gameplay?.Player;
            return player != null && (player.Get_ActiveTool == item || player.Get_HoldingItem == item);
        }
        private void CopyTree(Transform original, Transform target, bool root)
        {
            nodes.Add(new Node { Source = original, Copy = target, SourceObject = original.gameObject, CopyObject = target.gameObject, Root = root });
            var renderer = original.GetComponent<MeshRenderer>(); var filter = original.GetComponent<MeshFilter>();
            if (renderer != null && filter != null && filter.sharedMesh != null) AddSurface(renderer, target, filter.sharedMesh, null);
            var skin = original.GetComponent<SkinnedMeshRenderer>();
            if (skin != null && skin.sharedMesh != null)
            {
                var holder = new GameObject("Baked skin"); holder.transform.SetParent(target, false);
                holder.layer = original.gameObject.layer;
                // Asset-only trash needs its authored rest mesh; its native animator and
                // bones have never been initialized by a spawned gameplay object.
                if (!suppressSource) AddSurface(skin, holder.transform, skin.sharedMesh, null);
                else
                {
                    var mesh = new Mesh();
                    AddSurface(skin, holder.transform, mesh, mesh);
                }
            }
            if (source != null) particles.Bind(source.transform, original, target);
            foreach (Transform child in original)
            {
                if (source is Tool_FishingRod rod && RemoteFishing.IsCatchBranch(rod, child)) continue;
                var copy = new GameObject(child.name); copy.transform.SetParent(target, false);
                CopyTree(child, copy.transform, false);
            }
        }
        private void AddSurface(Renderer original, Transform holder, Mesh mesh, Mesh? owned)
        {
            var copyMesh = holder.gameObject.AddComponent<MeshFilter>();
            copyMesh.sharedMesh = mesh;
            var copy = holder.gameObject.AddComponent<MeshRenderer>();
            var suppression = suppressSource ? new RendererSuppressionScope(original) : null;
            surfaces.Add(new Surface { Source = original, Copy = copy, ForceWasOff = suppression?.Original ?? original.forceRenderingOff,
                Suppression = suppression, Baked = owned,
                SourceMesh = original is MeshRenderer ? original.GetComponent<MeshFilter>() : null, CopyMesh = copyMesh });
            if (source != null && visual != null) line.BindIfFishingLine(source, original, copy, copyMesh, visual.transform);
        }
        internal void Reset()
        {
            // This is also called every ordinary frame after equipment is put away.
            if (surfaces.Count == 0 && nodes.Count == 0 && sourceObject == null && visual == null) return;
            ResetActive();
        }
        private void ResetActive()
        {
            List<Exception>? failures = null;
            void Attempt(Action run) { try { run(); } catch (Exception ex) { (failures ??= new List<Exception>()).Add(ex); } }
            Attempt(line.Reset);
            Attempt(particles.Reset);
            if (catchVisual != null) Attempt(catchVisual.Reset);
            foreach (var surface in surfaces)
            {
                if (surface.Suppression != null) Attempt(surface.Suppression.Dispose);
                if (surface.Baked != null) Attempt(() => UnityEngine.Object.Destroy(surface.Baked));
            }
            surfaces.Clear(); nodes.Clear(); source = null; sourceObject = null; suppressSource = false;
            if (visual != null)
            {
                var old = visual; visual = null;
                Attempt(() => old.SetActive(false)); Attempt(() => UnityEngine.Object.Destroy(old));
            }
            if (failures != null) throw new AggregateException("Remote held visual cleanup encountered failures.", failures);
        }
        public void Dispose() => Reset();
    }
}
