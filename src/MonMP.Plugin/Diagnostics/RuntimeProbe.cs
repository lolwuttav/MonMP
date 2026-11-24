using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class RuntimeProbe
    {
        private static readonly string[] InteractionDispatchHooks = {
            "get_isCanBeClickedWithToolInHand", "get_KeepOriginalMaterialsOnBlueHover", "Update_NameToAddBefore",
            "Update_NameToAddAfter", "GetName", "GetSimpleName", "customCrosshair", "HandInteractionAnimation",
            "CanUse", "MouseHover", "MouseOut", "ScrolledOn", "MouseDown", "MouseUp", "MouseRightDown",
            "MouseRightUp", "Use", "UseUp"
        };

        public InteractionCoverage Coverage { get; private set; } = null!;
        public string GameHash { get; private set; } = "";
        public bool Compatible { get; private set; }
        public string Status { get; private set; } = "Not checked";
        public string DirectoryPath => Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics");
        internal string? InactiveInteractionsPath { get; private set; }

        public void Validate()
        {
            Directory.CreateDirectory(DirectoryPath);
            var gameAssembly = typeof(Gameplay).Assembly;
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(gameAssembly.Location))
                GameHash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            var checks = new List<string>();
            bool healthy = true;
            Check(typeof(Gameplay), "Awake", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(Gameplay), "PauseGame", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(Gameplay), "ResumeGame", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(SaveSystem), "Save", true, new[] { typeof(House_Location) }, checks, ref healthy);
            Check(typeof(SaveSystem), "Load", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(Furniture_SwitchButton), "MouseDown", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(Furniture_Light), "TurnOn", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(Furniture_Light), "TurnOff", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(PlayerInteractable), "GrabInteractable", false, Type.EmptyTypes, checks, ref healthy);
            Check(typeof(PlayerInteractable), "UnGrabInteractable", false, new[] { typeof(bool) }, checks, ref healthy);
            Check(typeof(PlayerInteractable), "UseKeyDown", false, Type.EmptyTypes, checks, ref healthy);
            foreach (var method in PatchHealth.Required()) checks.Add("OK critical " + method.DeclaringType!.FullName + "." + method.Name);
            PatchHealth.ValidateFields();
            using (var stream = typeof(RuntimeProbe).Assembly.GetManifestResourceStream("MonMP.InteractionCoverage.tsv"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing interaction coverage manifest.")))
            {
                string manifest = reader.ReadToEnd(); var coverage = new InteractionCoverage(manifest); Coverage = coverage;
                var concreteTypes = gameAssembly.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters).ToArray();
                coverage.ValidateTypes(concreteTypes.Where(t => typeof(Interactable).IsAssignableFrom(t)).Select(t => t.FullName!),
                    concreteTypes.Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && !typeof(Interactable).IsAssignableFrom(t)).Select(t => t.FullName!));
                File.WriteAllText(Path.Combine(DirectoryPath, "interaction-coverage.tsv"), manifest);
                checks.Add("Coverage: " + coverage.Entries.Values.Count(row => row[1] != "session" && row[1] != "native-system") + " concrete interactions; " +
                    coverage.Entries.Values.Count(row => row[1] == "native-system") + " native systems; " + coverage.PendingCount + " pending review.");
            }
            Compatible = healthy && GameHash == SessionOptions.SupportedGameHash;
            Status = Compatible ? "Supported game build; patch targets verified" : "Unsupported build or missing patch targets; sessions disabled";
            var text = new StringBuilder()
                .AppendLine("MonMP " + SessionOptions.CurrentModVersion)
                .AppendLine("Unity: " + Application.unityVersion)
                .AppendLine("Game: " + Application.version)
                .AppendLine("Gameplay assembly SHA-256: " + GameHash)
                .AppendLine("Gameplay MVID: " + gameAssembly.ManifestModule.ModuleVersionId)
                .AppendLine("BepInEx: " + typeof(BaseUnityPlugin).Assembly.GetName().Version)
                .AppendLine("Harmony: " + typeof(Harmony).Assembly.GetName().Version)
                .AppendLine("LiteNetLib: " + typeof(LiteNetLib.NetManager).Assembly.GetName().Version)
                .AppendLine(Status);
            foreach (var check in checks) text.AppendLine(check);
            File.WriteAllText(Path.Combine(DirectoryPath, "runtime.txt"), text.ToString());
        }

        private static void Check(Type type, string name, bool isStatic, Type[] parameters, List<string> checks, ref bool healthy)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, parameters, null);
            bool ok = method != null && method.IsStatic == isStatic && method.ReturnType == typeof(void);
            healthy &= ok;
            checks.Add((ok ? "OK " : "MISSING ") + type.Name + "." + name + "(" + string.Join(",", parameters.Select(p => p.Name)) + ")");
        }

        public string DumpWorld()
        {
            Directory.CreateDirectory(DirectoryPath);
            var rows = new List<string>();
            var collisions = new List<string>();
            var registry = new IdentityRegistry();
            var transforms = new List<Transform>();
            var duplicateRootOrdinals = new Dictionary<Transform, int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach (var group in roots.GroupBy(root => root.name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                {
                    int ordinal = 0;
                    foreach (var root in group) duplicateRootOrdinals[root.transform] = ordinal++;
                }
                foreach (var root in roots) transforms.AddRange(root.GetComponentsInChildren<Transform>(true));
            }
            var hierarchy = new List<string>();
            var inactiveInteractions = new List<string>();
            foreach (var transform in transforms)
            {
                string path = CanonicalInventoryPath(transform, duplicateRootOrdinals);
                hierarchy.Add(path + "\t" + transform.gameObject.activeSelf);
                var ordinal = new Dictionary<Type, int>();
                foreach (var interactable in transform.GetComponents<Interactable>())
                {
                    if (interactable == null) continue;
                    Type type = interactable.GetType();
                    ordinal.TryGetValue(type, out int index); ordinal[type] = index + 1;
                    string identity = path + "/" + StaticId.Segment(type.FullName ?? type.Name, index) + "/interactable";
                    ulong id = StaticId.Hash(identity);
                    if (registry.Paths.TryGetValue(id, out string existing))
                    {
                        collisions.Add(id.ToString("X16", CultureInfo.InvariantCulture) + "\t" + existing + "\t" + identity);
                    }
                    else
                    {
                        registry.Register(identity);
                        rows.Add(id.ToString("X16", CultureInfo.InvariantCulture) + "\t" + identity);
                    }
                    if (!interactable.gameObject.activeInHierarchy)
                        inactiveInteractions.Add(InactiveInteractionRow(id, identity, path, interactable));
                }
            }
            rows.Sort(StringComparer.Ordinal); hierarchy.Sort(StringComparer.Ordinal);
            inactiveInteractions.Sort(StringComparer.Ordinal);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string output = Path.Combine(DirectoryPath, "world-" + stamp + ".tsv");
            InactiveInteractionsPath = Path.Combine(DirectoryPath, "inactive-interactions-" + stamp + ".tsv");
            File.WriteAllLines(output, new[] {
                "# Diagnostic candidates only. Runtime-spawned objects are not classified as static.",
                "# Compare two launches of the same save before accepting these IDs for replication.",
                "id\tcanonical-path" }.Concat(rows));
            File.WriteAllLines(Path.Combine(DirectoryPath, "hierarchy-" + stamp + ".tsv"), hierarchy);
            File.WriteAllLines(Path.Combine(DirectoryPath, "world-collisions-" + stamp + ".tsv"),
                new[] { "id\texisting-path\tcandidate-path" }.Concat(collisions));
            File.WriteAllLines(InactiveInteractionsPath, new[] {
                "id\tidentity-path\tgameobject-path\ttype\tactive-self\tactive-in-hierarchy\tcomponent-enabled\tdisposition\tadapter\texecution\tlate-join\ttests\tdispatch\tgap-flags"
            }.Concat(inactiveInteractions));
            int inactiveGapCount = inactiveInteractions.Count(row => {
                string flags = row.Split('\t')[13];
                return flags.Contains("pending-review") || flags.Contains("partial-adapter") || flags.Contains("missing-coverage");
            });
            return "Dumped " + transforms.Count + " transforms and " + rows.Count + " interactables; identity collisions: " + collisions.Count +
                "; inactive interactables: " + inactiveInteractions.Count + "; inactive coverage gaps: " + inactiveGapCount + ". " + output;
        }

        private string InactiveInteractionRow(ulong id, string identity, string path, Interactable item)
        {
            Type type = item.GetType();
            string typeName = type.FullName ?? type.Name;
            string disposition = "missing";
            string adapter = "-", execution = "-", lateJoin = "-", tests = "-";
            string gap = "missing-coverage";
            if (Coverage.Entries.TryGetValue(typeName, out var coverage))
            {
                disposition = coverage[2]; adapter = coverage[3]; execution = coverage[4];
                lateJoin = coverage[5]; tests = coverage[6];
                gap = disposition == "pending" ? "pending-review" : disposition == "partial" ? "partial-adapter" : "-";
            }
            if (!item.enabled) gap = gap == "-" ? "disabled-component" : gap + ";disabled-component";
            return id.ToString("X16", CultureInfo.InvariantCulture) + "\t" + identity + "\t" + path + "\t" + typeName + "\t" +
                item.gameObject.activeSelf + "\t" + item.gameObject.activeInHierarchy + "\t" + item.enabled + "\t" +
                disposition + "\t" + adapter + "\t" + execution + "\t" + lateJoin + "\t" + tests + "\t" +
                DispatchTargets(type) + "\t" + gap;
        }

        private static string DispatchTargets(Type concreteType)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var baseType = typeof(Interactable);
            var parts = new List<string>();
            foreach (MethodInfo baseMethod in baseType.GetMethods(flags)
                .Where(method => method.IsVirtual && InteractionDispatchHooks.Contains(method.Name, StringComparer.Ordinal))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ThenBy(method => string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName)), StringComparer.Ordinal))
            {
                MethodInfo? target = null;
                for (Type? current = concreteType; current != null && baseType.IsAssignableFrom(current); current = current.BaseType)
                {
                    target = current.GetMethods(flags).FirstOrDefault(candidate =>
                        candidate.Name == baseMethod.Name && candidate.IsVirtual &&
                        candidate.GetBaseDefinition() == baseMethod.GetBaseDefinition() &&
                        candidate.GetParameters().Select(parameter => parameter.ParameterType)
                            .SequenceEqual(baseMethod.GetParameters().Select(parameter => parameter.ParameterType)));
                    if (target != null) break;
                }
                target = target ?? baseMethod;
                string parameters = string.Join(",", baseMethod.GetParameters().Select(parameter => parameter.ParameterType.FullName));
                parts.Add(baseMethod.Name + "(" + parameters + ")=" +
                    (target.DeclaringType?.FullName ?? target.DeclaringType?.Name) + "." + target.Name);
            }
            return string.Join(";", parts);
        }

        // Same-name scene roots can share Unity's root sibling index in this game.
        // Disambiguate those roots for diagnostics only. Unique roots and all network
        // identity callers retain CanonicalPath's established segment values.
        private static string CanonicalInventoryPath(Transform transform, Dictionary<Transform, int> duplicateRootOrdinals)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                int sibling = current.GetSiblingIndex();
                if (current.parent == null && duplicateRootOrdinals.TryGetValue(current, out int ordinal)) sibling = ordinal;
                segments.Push(StaticId.Segment(current.name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"), sibling));
            }
            var scene = transform.gameObject.scene;
            return StaticId.Segment(scene.path, scene.buildIndex) + "/" + string.Join("/", segments);
        }

        internal static string CanonicalPath(Transform transform)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
                segments.Push(StaticId.Segment(current.name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"), current.GetSiblingIndex()));
            var scene = transform.gameObject.scene;
            return StaticId.Segment(scene.path, scene.buildIndex) + "/" + string.Join("/", segments);
        }
    }
}
