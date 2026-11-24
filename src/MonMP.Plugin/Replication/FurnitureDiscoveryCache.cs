using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Replication
{
    // Discovery still observes every native component. Reuse identity strings only after
    // validating their complete hierarchy in this discovery pass, including forced clicks.
    internal sealed class FurnitureDiscoveryCache
    {
        private sealed class Node
        {
            public Transform Transform = null!;
            public Node? Parent;
            public string Name = "", ScenePath = "", Canonical = "", RawSegment = "", Stable = "";
            public int Sibling, SceneHandle, BuildIndex, Seen, Revision, ParentRevision;
            public int StableSeen, StableOrdinal, StableParentRevision, StableNodeRevision, StableRevision;
        }
        private sealed class ComponentEntry
        {
            public int Seen, OrdinalSeen, Ordinal, SceneRevision, SceneOrdinal, RelativeRevision, RelativeOrdinal;
            public string SceneRole = "", SceneIdentity = "", RelativeRole = "", Relative = "";
            public Transform? RelativeRoot;
            public ulong ItemEntity;
            public string ItemRelative = "", ItemIdentity = "";
            public ulong RadioEntity;
            public string RadioIdentity = "", RadioPath = "";
            public Transform? RadioItem;
            public readonly List<Component> Components = new List<Component>();
        }
        private sealed class Siblings
        {
            public int Seen;
            public readonly Dictionary<Transform, int> Ordinals = new Dictionary<Transform, int>(ReferenceComparer<Transform>.Instance);
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<GameObject> Roots = new List<GameObject>();
        }
        private sealed class SceneMetadata { public int Seen, BuildIndex; public string Path = ""; }
        private readonly Dictionary<Transform, Node> nodes = new Dictionary<Transform, Node>(ReferenceComparer<Transform>.Instance);
        private readonly Dictionary<Component, ComponentEntry> components = new Dictionary<Component, ComponentEntry>(ReferenceComparer<Component>.Instance);
        private readonly Dictionary<Transform, Siblings> siblingSets = new Dictionary<Transform, Siblings>(ReferenceComparer<Transform>.Instance);
        private readonly Dictionary<int, Siblings> rootSets = new Dictionary<int, Siblings>();
        private readonly Dictionary<int, SceneMetadata> sceneMetadata = new Dictionary<int, SceneMetadata>();
        private readonly List<Transform> deadNodes = new List<Transform>();
        private readonly List<Component> deadComponents = new List<Component>();
        private readonly List<int> deadScenes = new List<int>();
        private int pass, revision;
        internal int PathBuilds { get; private set; }
        internal int HierarchyVisits { get; private set; }
        internal int ComponentQueries { get; private set; }
        internal int SceneQueries { get; private set; }
        internal int Count => components.Count;

        internal void BeginPass()
        {
            pass++;
            PathBuilds = 0; HierarchyVisits = 0; ComponentQueries = 0; SceneQueries = 0;
        }
        private Node Observe(Transform transform)
        {
            if (!nodes.TryGetValue(transform, out var node))
            { node = new Node { Transform = transform, Seen = -1 }; nodes.Add(transform, node); }
            if (node.Seen == pass) return node;
            node.Seen = pass; HierarchyVisits++;
            var parent = transform.parent == null ? null : Observe(transform.parent);
            var scene = transform.gameObject.scene;
            if (!sceneMetadata.TryGetValue(scene.handle, out var metadata))
            { metadata = new SceneMetadata(); sceneMetadata.Add(scene.handle, metadata); }
            if (metadata.Seen != pass)
            { metadata.Seen = pass; metadata.Path = scene.path; metadata.BuildIndex = scene.buildIndex; SceneQueries++; }
            string name = transform.name;
            int sibling = transform.GetSiblingIndex();
            if (node.Revision == 0 || !ReferenceEquals(node.Parent, parent) || node.Name != name || node.Sibling != sibling ||
                node.SceneHandle != scene.handle || node.ScenePath != metadata.Path || node.BuildIndex != metadata.BuildIndex ||
                node.ParentRevision != (parent?.Revision ?? 0))
            {
                node.Parent = parent; node.Name = name; node.Sibling = sibling;
                node.SceneHandle = scene.handle; node.ScenePath = metadata.Path; node.BuildIndex = metadata.BuildIndex;
                node.ParentRevision = parent?.Revision ?? 0; node.Revision = ++revision;
                node.RawSegment = StaticId.Segment(name, sibling);
                string segment = StaticId.Segment(Escape(name), sibling);
                node.Canonical = parent == null ? StaticId.Segment(metadata.Path, metadata.BuildIndex) + "/" + segment : parent.Canonical + "/" + segment;
                PathBuilds++;
            }
            return node;
        }
        internal string CanonicalPath(Transform transform) => Observe(transform).Canonical;
        private static string Escape(string name) => name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        private ComponentEntry Entry(Component component)
        {
            if (!components.TryGetValue(component, out var entry))
            { entry = new ComponentEntry(); components.Add(component, entry); }
            entry.Seen = pass;
            return entry;
        }
        private int Ordinal(Component component, ComponentEntry entry)
        {
            if (entry.OrdinalSeen == pass) return entry.Ordinal;
            entry.OrdinalSeen = pass; ComponentQueries++;
            entry.Components.Clear();
            component.GetComponents(component.GetType(), entry.Components);
            int ordinal = 0;
            while (ordinal < entry.Components.Count && entry.Components[ordinal] != component) ordinal++;
            entry.Ordinal = ordinal;
            return ordinal;
        }
        internal string SceneIdentity(Component component, string role)
        {
            var node = Observe(component.transform); var entry = Entry(component);
            int ordinal = Ordinal(component, entry);
            if (entry.SceneIdentity.Length == 0 || entry.SceneRevision != node.Revision || entry.SceneOrdinal != ordinal || entry.SceneRole != role)
            {
                entry.SceneRevision = node.Revision; entry.SceneOrdinal = ordinal; entry.SceneRole = role;
                entry.SceneIdentity = node.Canonical + "/" + role + "/" + ordinal;
            }
            return entry.SceneIdentity;
        }
        internal string RelativeRole(Component component, Transform root, string role)
        {
            var node = Observe(component.transform); var entry = Entry(component);
            int ordinal = Ordinal(component, entry);
            if (!ReferenceEquals(entry.RelativeRoot, root) || entry.RelativeRevision != node.Revision || entry.RelativeOrdinal != ordinal || entry.RelativeRole != role)
            {
                entry.RelativeRoot = root; entry.RelativeRevision = node.Revision; entry.RelativeOrdinal = ordinal; entry.RelativeRole = role;
                string relative = "";
                var current = node;
                while (current != null && current.Transform != root)
                { relative = relative.Length == 0 ? current.RawSegment : current.RawSegment + "/" + relative; current = current.Parent; }
                entry.Relative = current == null ? "" : (relative.Length == 0 ? "$root" : relative) + "/" + role + "/" + ordinal;
            }
            return entry.Relative;
        }
        internal string RadioIdentity(Component radio, Transform? item, ulong entity)
        {
            var entry = Entry(radio);
            string path = entity == 0 ? StablePath(radio.transform) : "";
            if (entry.RadioIdentity.Length == 0 || !ReferenceEquals(entry.RadioItem, item) || entry.RadioEntity != entity || entry.RadioPath != path)
            {
                entry.RadioItem = item; entry.RadioEntity = entity; entry.RadioPath = path;
                entry.RadioIdentity = entity == 0 ? "path/" + path : "item/" + entity.ToString("X16");
            }
            return entry.RadioIdentity;
        }
        internal string ItemIdentity(Component component, Transform root, ulong entity, string role, out string relative)
        {
            relative = RelativeRole(component, root, role);
            if (relative.Length == 0) return "";
            var entry = Entry(component);
            if (entry.ItemIdentity.Length == 0 || entry.ItemEntity != entity || entry.ItemRelative != relative)
            {
                entry.ItemEntity = entity; entry.ItemRelative = relative;
                entry.ItemIdentity = "item/" + entity.ToString("X16") + "/" + relative;
            }
            return entry.ItemIdentity;
        }
        internal string StablePath(Transform transform) => Stable(Observe(transform));
        private string Stable(Node node)
        {
            if (node.StableSeen == pass) return node.Stable;
            node.StableSeen = pass;
            string parentPath = node.Parent == null ? "" : Stable(node.Parent);
            int ordinal = StableOrdinal(node);
            int parentRevision = node.Parent?.StableRevision ?? 0;
            if (node.StableRevision == 0 || node.StableNodeRevision != node.Revision || node.StableParentRevision != parentRevision || node.StableOrdinal != ordinal)
            {
                node.StableNodeRevision = node.Revision; node.StableParentRevision = parentRevision; node.StableOrdinal = ordinal;
                node.StableRevision = ++revision;
                string segment = StaticId.Segment(Escape(node.Name), ordinal);
                node.Stable = node.Parent == null ? StaticId.Segment(node.ScenePath, node.BuildIndex) + "/" + segment : parentPath + "/" + segment;
                PathBuilds++;
            }
            return node.Stable;
        }
        private int StableOrdinal(Node node)
        {
            Siblings siblings;
            if (node.Parent != null)
            {
                if (!siblingSets.TryGetValue(node.Parent.Transform, out siblings!))
                { siblings = new Siblings(); siblingSets.Add(node.Parent.Transform, siblings); }
            }
            else if (!rootSets.TryGetValue(node.SceneHandle, out siblings!))
            { siblings = new Siblings(); rootSets.Add(node.SceneHandle, siblings); }
            if (siblings.Seen != pass)
            {
                siblings.Seen = pass; siblings.Ordinals.Clear(); siblings.Counts.Clear();
                if (node.Parent == null)
                {
                    siblings.Roots.Clear(); node.Transform.gameObject.scene.GetRootGameObjects(siblings.Roots);
                    foreach (var root in siblings.Roots) AddSibling(siblings, root.transform);
                }
                else
                    for (int i = 0; i < node.Parent.Transform.childCount; i++) AddSibling(siblings, node.Parent.Transform.GetChild(i));
            }
            return siblings.Ordinals.TryGetValue(node.Transform, out int ordinal) ? ordinal : 0;
        }
        private static void AddSibling(Siblings siblings, Transform transform)
        {
            string name = transform.name;
            siblings.Counts.TryGetValue(name, out int ordinal);
            siblings.Ordinals[transform] = ordinal; siblings.Counts[name] = ordinal + 1;
        }
        internal void EndPass()
        {
            deadNodes.Clear();
            foreach (var pair in nodes) if (pair.Value.Seen != pass) deadNodes.Add(pair.Key);
            foreach (var key in deadNodes) nodes.Remove(key);
            deadComponents.Clear();
            foreach (var pair in components) if (pair.Value.Seen != pass) deadComponents.Add(pair.Key);
            foreach (var key in deadComponents) components.Remove(key);
            deadNodes.Clear();
            foreach (var pair in siblingSets) if (pair.Value.Seen != pass) deadNodes.Add(pair.Key);
            foreach (var key in deadNodes) siblingSets.Remove(key);
            deadScenes.Clear();
            foreach (var pair in rootSets) if (pair.Value.Seen != pass) deadScenes.Add(pair.Key);
            foreach (var key in deadScenes) rootSets.Remove(key);
            deadScenes.Clear();
            foreach (var pair in sceneMetadata) if (pair.Value.Seen != pass) deadScenes.Add(pair.Key);
            foreach (var key in deadScenes) sceneMetadata.Remove(key);
            // Scratch lists must not retain removed Unity objects between scans.
            deadNodes.Clear(); deadComponents.Clear(); deadScenes.Clear();
        }
        internal void Clear()
        {
            nodes.Clear(); components.Clear(); siblingSets.Clear(); rootSets.Clear(); sceneMetadata.Clear();
            deadNodes.Clear(); deadComponents.Clear(); deadScenes.Clear();
        }
    }

    internal sealed class FurnitureSceneCatalog<T> where T : class
    {
        private sealed class Candidate { public string Path = ""; public int Seen; }
        private readonly Dictionary<T, Candidate> candidates = new Dictionary<T, Candidate>(ReferenceComparer<T>.Instance);
        private readonly Dictionary<string, T> unique = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly HashSet<string> ambiguous = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<T> removed = new List<T>();
        private int pass;
        private bool dirty;
        internal int Rebuilds { get; private set; }
        internal IEnumerable<string> AmbiguousPaths => ambiguous;
        internal bool IsUnique(string path) => unique.ContainsKey(path);
        internal void BeginPass() => pass++;
        internal void Observe(T component, string path)
        {
            if (!candidates.TryGetValue(component, out var candidate))
            { candidate = new Candidate(); candidates.Add(component, candidate); dirty = true; }
            if (candidate.Path != path) { candidate.Path = path; dirty = true; }
            candidate.Seen = pass;
        }
        internal void EndPass()
        {
            removed.Clear();
            foreach (var pair in candidates) if (pair.Value.Seen != pass) removed.Add(pair.Key);
            foreach (var key in removed) { candidates.Remove(key); dirty = true; }
            removed.Clear();
            if (!dirty) return;
            dirty = false; Rebuilds++; unique.Clear(); ambiguous.Clear();
            foreach (var pair in candidates)
            {
                string path = pair.Value.Path;
                if (ambiguous.Contains(path)) continue;
                if (!unique.TryGetValue(path, out var prior)) unique.Add(path, pair.Key);
                else if (!ReferenceEquals(prior, pair.Key)) { unique.Remove(path); ambiguous.Add(path); }
            }
        }
        internal void Clear() { candidates.Clear(); unique.Clear(); ambiguous.Clear(); removed.Clear(); dirty = false; }
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
