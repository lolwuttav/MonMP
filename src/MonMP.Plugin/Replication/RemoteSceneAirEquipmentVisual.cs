using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Geometry only: no native tool/tube scripts, physics or gameplay callbacks.
    internal sealed class RemoteSceneAirEquipmentVisual : IDisposable
    {
        private sealed class Surface
        {
            internal Renderer Source = null!;
            internal MeshRenderer? Copy;
            internal bool OriginalOff;
            internal Mesh? Baked;
            internal MeshFilter? SourceMesh;
            internal MeshFilter? CopyMesh;
            internal bool MaterialsCaptured;
            internal readonly List<Material> Materials = new List<Material>();
        }
        private sealed class Node
        {
            internal Transform Source = null!, Copy = null!;
            internal GameObject SourceObject = null!, CopyObject = null!;
        }
        private readonly List<Surface> surfaces = new List<Surface>();
        private readonly HashSet<Renderer> knownSources = new HashSet<Renderer>();
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<Renderer> rendererScratch = new List<Renderer>();
        private readonly List<Material> materialScratch = new List<Material>();
        private InteractableTools? source;
        private GameObject? root;
        private LineRenderer? hose;
        private TubeController? nativeTube;
        private GameObject? rolledSource;
        private Vector3 hoseEndpointLocal;
        private bool hoseEndpointKnown;
        private readonly List<(Renderer Renderer, bool Off)> restored = new List<(Renderer, bool)>();
        private static readonly System.Reflection.FieldInfo PumpTubeField = AccessTools.Field(typeof(Tool_Air_TirePump), "tubeController") ??
            throw new InvalidOperationException("Unsupported native air gun tube field.");
        private static readonly System.Reflection.FieldInfo PumpRolledField = AccessTools.Field(typeof(Tool_Air_TirePump), "TubeRolled") ??
            throw new InvalidOperationException("Unsupported native rolled hose field.");
        private static readonly System.Reflection.FieldInfo PumpEndField = AccessTools.Field(typeof(Tool_Air_TirePump), "TubeEnd") ??
            throw new InvalidOperationException("Unsupported native air gun hose endpoint field.");
        private static readonly System.Reflection.FieldInfo ImpactTubeField = AccessTools.Field(typeof(Tool_Air_Impact), "tubeController") ??
            throw new InvalidOperationException("Unsupported native impact-gun tube field.");
        private static readonly System.Reflection.FieldInfo ImpactRolledField = AccessTools.Field(typeof(Tool_Air_Impact), "TubeRolled") ??
            throw new InvalidOperationException("Unsupported native impact-gun rolled hose field.");
        private static readonly System.Reflection.FieldInfo ImpactEndField = AccessTools.Field(typeof(Tool_Air_Impact), "TubeEnd") ??
            throw new InvalidOperationException("Unsupported native impact-gun hose endpoint field.");
        private static readonly System.Reflection.FieldInfo TubeCustomMaterialField = AccessTools.Field(typeof(TubeController), "CustomMaterial") ??
            throw new InvalidOperationException("Unsupported native tube material field.");
        private static readonly System.Reflection.FieldInfo FuelTubeField = AccessTools.Field(typeof(Kaligaz_FuelGun), "tubeController") ??
            throw new InvalidOperationException("Unsupported native fuel gun tube field.");
        private static readonly System.Reflection.FieldInfo TubeEndField = AccessTools.Field(typeof(TubeController), "TargetEnd") ??
            throw new InvalidOperationException("Unsupported native tube endpoint field.");
        internal bool Active => root != null && root.activeSelf;
        internal Transform? PresentedTransform => Active ? root!.transform : null;
        internal bool PresentationOnly => root != null && root.GetComponentsInChildren<Component>(true).All(component =>
            component is Transform || component is MeshFilter || component is MeshRenderer || component is LineRenderer);
        internal bool NativeFlagsRestored => !Active && restored.All(value => value.Renderer == null || value.Renderer.forceRenderingOff == value.Off);

        internal bool Present(Tool_Air_TirePump tool, Vector3 position, Quaternion rotation)
            => PresentCore(tool, position, rotation, PumpTubeField, PumpRolledField, PumpEndField);

        internal bool PresentImpact(Tool_Air_Impact tool, Vector3 position, Quaternion rotation)
            => PresentCore(tool, position, rotation, ImpactTubeField, ImpactRolledField, ImpactEndField);

        internal bool PresentFuel(Kaligaz_FuelGun tool, Vector3 position, Quaternion rotation)
            => PresentCore(tool, position, rotation, FuelTubeField, null, null);

        private static Transform? Endpoint(InteractableTools tool, TubeController? tube, System.Reflection.FieldInfo? field) =>
            field != null ? field.GetValue(tool) as Transform : tube == null ? null : TubeEndField.GetValue(tube) as Transform;

        private static bool TryGetWorldHoseEndpoints(Transform? baseJoint, Transform remoteTool, Vector3 endpointLocal,
            out Vector3 start, out Vector3 end)
        {
            start = default;
            end = default;
            if (baseJoint == null) return false;
            start = baseJoint.position;
            end = remoteTool.TransformPoint(endpointLocal);
            return true;
        }

        private bool PresentCore(InteractableTools tool, Vector3 position, Quaternion rotation,
            System.Reflection.FieldInfo tubeField, System.Reflection.FieldInfo? rolledField, System.Reflection.FieldInfo? endField)
        {
            try
            {
                var currentTube = tubeField.GetValue(tool) as TubeController;
                if (source != tool || root == null || nativeTube != currentTube)
                {
                    Reset(); source = tool;
                    nativeTube = currentTube;
                    var rolled = rolledField?.GetValue(tool) as GameObject;
                    var endpoint = Endpoint(tool, nativeTube, endField);
                    if (nativeTube == null || endpoint == null || nativeTube.Get_BaseJoint == null) { Reset(); return false; }
                    root = new GameObject("MonMP Scene Air Equipment Presentation");
                    // The native renderer is updated by TubeController and cannot be copied as
                    // a mesh snapshot; the remote hose below is the presentation-only renderer.
                    CopyTree(tool.transform, root.transform, true, rolled?.transform, nativeTube.transform);
                    if (surfaces.All(surface => surface.Copy == null)) { Reset(); return false; }
                    // The stored roll is a separate native drawing surface, never an
                    // equipped tool/collider. Hide its drawing while the hose is in use.
                    DiscoverRolled(rolled);
                    nativeTube.GetComponentsInChildren(true, rendererScratch);
                    foreach (var renderer in rendererScratch) HideOnly(renderer);
                    var hoseObject = new GameObject("Remote air hose"); hoseObject.transform.SetParent(root.transform, false);
                    hose = hoseObject.AddComponent<LineRenderer>(); hose.useWorldSpace = true; hose.positionCount = 12;
                    hose.startWidth = hose.endWidth = 0.03f;
                    var hoseMaterial = TubeCustomMaterialField.GetValue(nativeTube) as Material;
                    if (hoseMaterial == null)
                        foreach (var renderer in rendererScratch)
                        {
                            renderer.GetSharedMaterials(materialScratch);
                            hoseMaterial = materialScratch.FirstOrDefault(material => material != null);
                            if (hoseMaterial != null) break;
                        }
                    hose.sharedMaterial = hoseMaterial ?? Singleton<GameAssets>.i.Material_Rubber;
                    rendererScratch.Clear(); materialScratch.Clear();
                }
                root!.transform.SetPositionAndRotation(position, rotation);
                root.transform.localScale = tool.transform.lossyScale;
                foreach (var node in nodes)
                {
                    if (node.Source == null || node.Copy == null) continue;
                    node.Copy.localPosition = node.Source.localPosition;
                    node.Copy.localRotation = node.Source.localRotation;
                    node.Copy.localScale = node.Source.localScale;
                    if (node.CopyObject.activeSelf != node.SourceObject.activeSelf)
                        node.CopyObject.SetActive(node.SourceObject.activeSelf);
                }
                // TubeController.Start may run only after a later activation and create its
                // Renderer then. Keep every late native renderer hidden while the independent
                // remote hose is shown, and restore each exact original flag on Reset.
                var currentRolled = rolledField?.GetValue(tool) as GameObject;
                DiscoverRolled(currentRolled);
                // Native TubeController.Start creates TubeRenderer's required MeshRenderer on its
                // own object, once. Check that exact component for late Start/replacement instead
                // of walking the entire hose and stored-roll hierarchies on every presented frame.
                var liveTubeRenderer = nativeTube!.GetComponent<MeshRenderer>();
                if (liveTubeRenderer != null) HideOnly(liveTubeRenderer);
                foreach (var surface in surfaces)
                {
                    if (surface.Source == null) continue;
                    if (surface.Copy != null)
                    {
                        surface.Copy.enabled = surface.Source.enabled;
                        surface.Copy.forceRenderingOff = surface.OriginalOff;
                        surface.Source.GetSharedMaterials(materialScratch);
                        bool changed = !surface.MaterialsCaptured || materialScratch.Count != surface.Materials.Count;
                        if (!changed)
                            for (int i = 0; i < materialScratch.Count; i++)
                                if (materialScratch[i] != surface.Materials[i]) { changed = true; break; }
                        if (changed)
                        {
                            surface.Copy.sharedMaterials = materialScratch.ToArray();
                            surface.Materials.Clear(); surface.Materials.AddRange(materialScratch);
                            surface.MaterialsCaptured = true;
                        }
                        surface.Copy.shadowCastingMode = surface.Source.shadowCastingMode;
                        surface.Copy.receiveShadows = surface.Source.receiveShadows;
                        if (surface.Baked != null && surface.Source is SkinnedMeshRenderer skin)
                        {
                            // The copy retains renderer/ancestor scale; compensate it in the baked vertices.
                            skin.BakeMesh(surface.Baked, true);
                        }
                        else if (surface.CopyMesh != null)
                        {
                            if (surface.SourceMesh == null) surface.SourceMesh = surface.Source.GetComponent<MeshFilter>();
                            if (surface.SourceMesh != null && surface.CopyMesh.sharedMesh != surface.SourceMesh.sharedMesh)
                                surface.CopyMesh.sharedMesh = surface.SourceMesh.sharedMesh;
                        }
                    }
                    if (!surface.Source.forceRenderingOff) surface.Source.forceRenderingOff = true;
                }
                var currentEndpoint = Endpoint(tool, nativeTube, endField);
                if (currentEndpoint == null) { Reset(); return false; }
                hoseEndpointLocal = tool.transform.InverseTransformPoint(currentEndpoint.position);
                hoseEndpointKnown = true;
                if (!RefreshHose()) { Reset(); return false; }
                return true;
            }
            catch (Exception ex)
            {
                Reset(); Debug.LogWarning("MonMP scene air equipment presentation failed: " + ex.Message); return false;
            }
        }
        internal void Translate(Vector3 correction)
        {
            if (!Active) return;
            try
            {
                // Only the independent drawing root moves. Keep the compressor/tank-side
                // anchor native and redraw the world-space line in this same render frame.
                root!.transform.position += correction;
                if (!RefreshHose()) Reset();
            }
            catch (Exception ex)
            {
                Reset(); Debug.LogWarning("MonMP scene air equipment correction failed: " + ex.Message);
            }
        }
        private bool RefreshHose()
        {
            if (root == null || hose == null || nativeTube == null || !hoseEndpointKnown ||
                !TryGetWorldHoseEndpoints(nativeTube.Get_BaseJoint, root.transform,
                    hoseEndpointLocal, out Vector3 start, out Vector3 end)) return false;
            for (int i = 0; i < hose.positionCount; i++)
            {
                float t = i / (float)(hose.positionCount - 1);
                hose.SetPosition(i, Vector3.Lerp(start, end, t) + Vector3.down * (Mathf.Sin(t * Mathf.PI) * 0.25f));
            }
            return true;
        }
        private void DiscoverRolled(GameObject? rolled)
        {
            // The serialized stored roll is only activated/deactivated by the native air tools.
            // Rebuild its renderer membership if that exact source object is replaced.
            if (rolledSource == rolled) return;
            rolledSource = rolled;
            if (rolled == null) return;
            rolled.GetComponentsInChildren(true, rendererScratch);
            foreach (var renderer in rendererScratch) HideOnly(renderer);
            rendererScratch.Clear();
        }
        private void CopyTree(Transform original, Transform copy, bool isRoot, Transform? rolled, Transform tube)
        {
            if (original == rolled || original == tube || !isRoot && original.GetComponent<InteractableItems>() != null) return;
            if (!isRoot) nodes.Add(new Node { Source = original, Copy = copy, SourceObject = original.gameObject, CopyObject = copy.gameObject });
            copy.gameObject.layer = LayerMask.NameToLayer("Default");
            var mesh = original.GetComponent<MeshFilter>(); var renderer = original.GetComponent<MeshRenderer>();
            if (mesh?.sharedMesh != null && renderer != null) Add(renderer, copy, mesh.sharedMesh, null);
            var skin = original.GetComponent<SkinnedMeshRenderer>();
            if (skin?.sharedMesh != null)
            {
                var baked = new Mesh(); var child = new GameObject("Baked air gun skin"); child.transform.SetParent(copy, false);
                Add(skin, child.transform, baked, baked);
            }
            foreach (Transform child in original)
            {
                if (child == rolled || child == tube || child.GetComponent<InteractableItems>() != null) continue;
                var childCopy = new GameObject(child.name); childCopy.transform.SetParent(copy, false);
                CopyTree(child, childCopy.transform, false, rolled, tube);
            }
        }
        private void Add(Renderer original, Transform copy, Mesh mesh, Mesh? baked)
        {
            var filter = copy.gameObject.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = copy.gameObject.AddComponent<MeshRenderer>();
            surfaces.Add(new Surface { Source = original, Copy = renderer, OriginalOff = original.forceRenderingOff, Baked = baked,
                SourceMesh = original is MeshRenderer ? original.GetComponent<MeshFilter>() : null, CopyMesh = filter });
            knownSources.Add(original);
        }
        private void HideOnly(Renderer renderer)
        {
            if (!knownSources.Add(renderer)) return;
            surfaces.Add(new Surface { Source = renderer, OriginalOff = renderer.forceRenderingOff });
        }
        internal void Reset()
        {
            // RemotePlayers also calls this every idle frame after a tool is put away.
            // Keep the inactive path free of the cleanup closure and exception-list allocation.
            if (surfaces.Count == 0 && nodes.Count == 0 && source == null && root == null) return;
            ResetActive();
        }
        private void ResetActive()
        {
            if (surfaces.Count != 0) restored.Clear();
            List<Exception>? failures = null;
            void Attempt(Action run) { try { run(); } catch (Exception ex) { (failures ??= new List<Exception>()).Add(ex); } }
            foreach (var surface in surfaces)
            {
                if (surface.Source != null)
                {
                    Attempt(() => surface.Source.forceRenderingOff = surface.OriginalOff);
                    restored.Add((surface.Source, surface.OriginalOff));
                }
                if (surface.Baked != null) Attempt(() => UnityEngine.Object.Destroy(surface.Baked));
            }
            surfaces.Clear(); knownSources.Clear(); nodes.Clear(); rendererScratch.Clear(); materialScratch.Clear();
            source = null; nativeTube = null; hose = null; rolledSource = null;
            hoseEndpointKnown = false; hoseEndpointLocal = default;
            if (root != null)
            {
                var old = root; root = null;
                Attempt(() => old.SetActive(false)); Attempt(() => UnityEngine.Object.Destroy(old));
            }
            if (failures != null) throw new AggregateException("Scene air visual cleanup encountered failures.", failures);
        }
        public void Dispose() => Reset();
    }
}
