using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class StockAvatarBody
    {
        private static GameObject? npcTemplateRoot;
        private static GameObject? npcDefaultSource;
        private static readonly Dictionary<string, Library> npcLibraries = new Dictionary<string, Library>(StringComparer.Ordinal);
        private Renderer[] npcHeadwear = Array.Empty<Renderer>();
#if MONMP_DEVELOPMENT
        internal bool FabricReady => nativeShirtTexture || shirtFabric != null;
        internal bool TestNativeShirtTexture => nativeShirtTexture;
        internal GameObject ExperimentObject => body;
        internal int ExperimentHeadwearCount => npcHeadwear.Length;
        internal int ExperimentVisibleHeadwear => npcHeadwear.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy);
#endif

        private static Library? LoadModel(string key, out string reason)
        {
            key = NpcAvatarModels.Normalize(key);
            var basis = Load(out reason);
            if (basis == null) return null;
            if (key == "default") return basis;
            if (npcTemplateRoot == null || npcDefaultSource != basis.Template)
            {
                ClearModelTemplates();
                npcDefaultSource = basis.Template;
                npcTemplateRoot = new GameObject("MonMP Avatar Templates");
                npcTemplateRoot.SetActive(false);
            }
            if (!npcLibraries.TryGetValue(key, out var lib))
            {
                GameObject? template = null;
                try
                {
                    var model = NpcAvatarModels.Get(key);
                    var native = model.Roots.Select(name => basis.Candidates.FirstOrDefault(root => root != null && root.name == name &&
                        root.Find("Armature") != null && Compatible(root, basis.Bones))).FirstOrDefault(root => root != null);
                    if (native == null) throw new InvalidOperationException("compatible native model is not loaded: " + key);
                    string before = NpcSourceStamp(native);
                    template = UnityEngine.Object.Instantiate(native.gameObject, npcTemplateRoot!.transform, false);
                    template.name = "NpcTemplate-" + key;
                    template.SetActive(true);
                    // Parent stays inactive throughout cloning/stripping. No actor Awake,
                    // dialogue, collision, audio, native animator or AI may run.
                    StripNpcComponents(template);
                    foreach (Transform child in template.transform)
                    {
                        if (child.name == "Armature") continue;
                        if (!NpcAvatarModels.Clothing(child.name)) child.gameObject.SetActive(false);
                        if (child.name == "Actor.001") child.name = "Actor";
                    }
                    var skeleton = template.transform.Find("Armature");
                    var byName = skeleton.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name)
                        .ToDictionary(g => g.Key, g => g.First());
                    // Static native hats sometimes live beside the skeleton rather
                    // than under Head. The body's bind pose, rather than a live NPC's
                    // turned head or the avatar's current look, defines their fitting.
                    var actor = template.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Actor");
                    int headIndex = Array.IndexOf(actor.bones, byName["Head"]);
                    if (headIndex < 0) throw new InvalidOperationException("NPC body has no bound head.");
                    var headBindWorld = actor.transform.localToWorldMatrix * actor.sharedMesh.bindposes[headIndex].inverse;
                    foreach (var renderer in template.GetComponentsInChildren<MeshRenderer>(true))
                        if (NpcAvatarModels.Headwear(renderer.name))
                        {
                            if (key == "chef" && renderer.name == "Cuisinier") renderer.gameObject.SetActive(true);
                            if (!renderer.transform.IsChildOf(byName["Head"]))
                            {
                                var local = headBindWorld.inverse * renderer.transform.localToWorldMatrix;
                                renderer.transform.SetParent(byName["Head"], false);
                                renderer.transform.localPosition = local.GetColumn(3);
                                renderer.transform.localRotation = local.rotation;
                                renderer.transform.localScale = local.lossyScale;
                            }
                        }
                    foreach (var renderer in skeleton.GetComponentsInChildren<Renderer>(true))
                        if (!NpcAvatarModels.Headwear(renderer.name) && !renderer.name.StartsWith("Hair", StringComparison.Ordinal)) renderer.enabled = false;
                    // The native officer is offset down 13 cm and tilted at the
                    // Armature for his NPC stance. Our shared player poses already
                    // supply that stance: retain the ordinary player rig placement.
                    // Do this after binding static headwear, and only on the clone.
                    if (key == "police")
                    {
                        var reference = basis.Template.transform.Find("Armature");
                        skeleton.localPosition = reference.localPosition;
                        skeleton.localRotation = reference.localRotation;
                        skeleton.localScale = reference.localScale;
                    }
                    // Keep this torso's own native garment and skin fit. Only its private
                    // material/UV view is replaced; no common shirt is overlaid on an NPC body.
                    var garment = template.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .First(r => r.name.StartsWith("Shirt_", StringComparison.Ordinal) && r.enabled && ActiveWithinTemplate(r.transform, template.transform));
                    foreach (var other in template.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (other != garment && other.name.StartsWith("Shirt_", StringComparison.Ordinal))
                        { other.gameObject.SetActive(false); other.name = "UnusedNative_" + other.name; }
                    garment.name = "Shirt_LongSleeve";
                    if (NpcSourceStamp(native) != before) throw new InvalidOperationException("NPC source changed during appearance construction.");
                    lib = new Library
                    {
                        Template = template, Bones = basis.Bones, Walk = basis.Walk,
                        Stand = basis.Stand, StandHips = basis.StandHips, Sit = basis.Sit, SitHips = basis.SitHips,
                        Chair = basis.Chair, ChairHips = basis.ChairHips, Drive = basis.Drive, DriveHips = basis.DriveHips,
                        Sources = "npc=" + key + ", native=" + native.name + ", colored-plaid; " + basis.Sources,
                        UsesModelClothing = true
                    };
                    npcLibraries.Add(key, lib);
                }
                catch (Exception ex)
                {
                    if (template != null) UnityEngine.Object.Destroy(template);
                    reason = "NPC " + key + " unavailable: " + ex.Message;
                    return null;
                }
            }
            reason = lib.Sources;
            return lib;
        }
        private static bool ActiveWithinTemplate(Transform node, Transform root)
        {
            // The scene NPC's schedule may deactivate its root. Apparel selection
            // depends on the clothing's own state, never that NPC's current presence.
            for (; node != null && node != root; node = node.parent) if (!node.gameObject.activeSelf) return false;
            return node == root;
        }
        private static void StripNpcComponents(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (!(component is Transform) && !(component is MeshRenderer) && !(component is SkinnedMeshRenderer) && !(component is MeshFilter))
                    UnityEngine.Object.DestroyImmediate(component);
        }
        private static string NpcSourceStamp(Transform native)
        {
            // Immediate before/after proof, not a comparison with naturally animated
            // NPC poses from another frame. Does not instantiate native materials.
            return string.Join("|", native.GetComponentsInChildren<Transform>(true).Select(t => t.GetInstanceID() + ":" +
                t.localPosition.ToString("R") + t.localRotation.ToString("R") + t.localScale.ToString("R") + t.gameObject.activeSelf)) +
                string.Join("|", native.GetComponentsInChildren<Renderer>(true).Select(r => r.GetInstanceID() + ":" + r.enabled + ":" +
                    string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.GetInstanceID() + ":" +
                        (m.HasProperty("_Color") ? m.color.ToString("R") : ""))))) +
                string.Join("|", native.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r =>
                    (r.sharedMesh != null ? r.sharedMesh.GetInstanceID() : 0) + ":" +
                    (r.rootBone != null ? r.rootBone.GetInstanceID() : 0) + ":" + string.Join(",", r.bones.Select(b => b == null ? 0 : b.GetInstanceID()))));
        }
        private void RememberNpcHeadwear() => npcHeadwear = body.GetComponentsInChildren<Renderer>(true)
            .Where(r => r.enabled && ActiveWithinNpc(r.transform) && NpcAvatarModels.Headwear(r.name)).ToArray();
        private bool ActiveWithinNpc(Transform transform)
        {
            // Construction deliberately holds the external parent inactive. Test
            // native apparel's own state rather than that temporary outer gate.
            for (var node = transform; node != null && node != body.transform.parent; node = node.parent)
                if (!node.gameObject.activeSelf) return false;
            return true;
        }
        private void SetNpcHeadwear(bool visible)
        { foreach (var renderer in npcHeadwear) if (renderer != null) renderer.enabled = visible; }
        internal static void ClearModelTemplates()
        {
            npcLibraries.Clear(); npcDefaultSource = null; AvatarMeshData.Clear();
            if (npcTemplateRoot != null) UnityEngine.Object.Destroy(npcTemplateRoot);
            npcTemplateRoot = null;
        }
    }
}
