using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local presentation settings. These never change item physics, the native equip pose,
    // ownership, network records or another player's settings.
    internal sealed class HeldGripOverrides
    {
        private sealed class SceneModel
        {
            internal string Key = "", Shape = "";
        }
        private sealed class SceneItemComparer : IEqualityComparer<InteractableItems>
        {
            public bool Equals(InteractableItems? left, InteractableItems? right) => ReferenceEquals(left, right);
            public int GetHashCode(InteractableItems item) => RuntimeHelpers.GetHashCode(item);
        }
        // Unity's Mono weak-table lookup threw InvalidCastException after a native
        // scene reload. Keep exact managed identities for this scene/session only;
        // explicit lifecycle cleanup releases destroyed Unity wrappers and their graphs.
        private static readonly Dictionary<InteractableItems, SceneModel> sceneModels = new Dictionary<InteractableItems, SceneModel>(new SceneItemComparer());
        internal static void ClearSceneModels() => sceneModels.Clear();
        internal class Grip
        {
            public string Prefab = "", Variant = "default", Shape = "";
            public float X, Y, Z, Pitch, Yaw, Roll, Width;
            // Optional v1 extension. In explicit mode X/Y/Z is the actual right palm,
            // with a separately placed and rotated left palm in the same item frame.
            // Missing fields preserve legacy one-hand and width-based grips.
            public bool TwoHanded;
            public float LeftX, LeftY, LeftZ, LeftPitch, LeftYaw, LeftRoll;
            // Draft-only state preserves a deliberately zero-valued left grip across toggles/undo.
            [JsonIgnore] internal bool LeftInitialized;
            // Optional v1 extension: translation in the remote body's axes, rotation in the
            // item's own axes. Missing fields retain zero and preserve old grip-only files.
            public float ItemX, ItemY, ItemZ, ItemPitch, ItemYaw, ItemRoll;
            internal Vector3 Position => new Vector3(X, Y, Z);
            internal Vector3 Euler => new Vector3(Pitch, Yaw, Roll);
            internal Vector3 LeftPosition => new Vector3(LeftX, LeftY, LeftZ);
            internal Vector3 LeftEuler => new Vector3(LeftPitch, LeftYaw, LeftRoll);
            internal Vector3 ItemPosition => new Vector3(ItemX, ItemY, ItemZ);
            internal Vector3 ItemEuler => new Vector3(ItemPitch, ItemYaw, ItemRoll);
            internal bool HasItemOffset => ItemX != 0 || ItemY != 0 || ItemZ != 0 || ItemPitch != 0 || ItemYaw != 0 || ItemRoll != 0;
            internal Grip Copy() => (Grip)MemberwiseClone();
            // A copied calibration changes the destination draft's presentation only.
            // Its prefab, variant and shape still identify the selected destination tool.

#if MONMP_DEVELOPMENT
            internal void CopyPoseFrom(Grip source)
            {
                Validate(source);
                Set(source.Position, source.Euler, source.Width);
                LeftX = source.LeftX; LeftY = source.LeftY; LeftZ = source.LeftZ;
                LeftPitch = source.LeftPitch; LeftYaw = source.LeftYaw; LeftRoll = source.LeftRoll;
                TwoHanded = source.TwoHanded;
                LeftInitialized = source.LeftInitialized || source.TwoHanded ||
                    source.LeftX != 0 || source.LeftY != 0 || source.LeftZ != 0 ||
                    source.LeftPitch != 0 || source.LeftYaw != 0 || source.LeftRoll != 0;
                ItemX = source.ItemX; ItemY = source.ItemY; ItemZ = source.ItemZ;
                ItemPitch = source.ItemPitch; ItemYaw = source.ItemYaw; ItemRoll = source.ItemRoll;
            }
#endif


#if MONMP_DEVELOPMENT
            internal void Set(Vector3 position, Vector3 euler, float width)
            { X = position.x; Y = position.y; Z = position.z; Pitch = euler.x; Yaw = euler.y; Roll = euler.z; Width = width; }
#endif


#if MONMP_DEVELOPMENT
            internal void SetLeft(Vector3 position, Vector3 euler)
            { LeftX = position.x; LeftY = position.y; LeftZ = position.z; LeftPitch = euler.x; LeftYaw = euler.y; LeftRoll = euler.z; LeftInitialized = true; }
#endif

#if MONMP_DEVELOPMENT
            internal void EnableTwoHands(Vector3 leftPosition, Vector3 leftEuler)
            { SetLeft(leftPosition, leftEuler); TwoHanded = true; Width = 0; }
#endif
        }
        private sealed class Document
        {
            [JsonProperty(Required = Required.Always)]
            public int Version = 1;
            [JsonProperty(Required = Required.Always)]
            public List<Grip> Items = new List<Grip>();
        }
        private static readonly Dictionary<(string Prefab, string Variant), Grip> defaults = ReadDefaults();
        private readonly Dictionary<(string Prefab, string Variant), Grip> saved = new Dictionary<(string, string), Grip>();
        internal string Path { get; }
        internal string LoadStatus { get; private set; } = "No saved overrides.";
        internal int Count => saved.Count;
        internal static int DefaultCount => defaults.Count;
        internal HeldGripOverrides(string path)
        {
            Path = path;
            if (!File.Exists(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) throw new InvalidDataException("Override file exceeds 1 MiB.");
                int retired = 0;
                foreach (var pair in ReadDocument(File.ReadAllText(path)))
                    if (IsRetiredDefault(pair.Value)) retired++; else saved.Add(pair.Key, pair.Value);
                LoadStatus = "Loaded " + saved.Count + " saved overrides." +
                    (retired > 0 ? " Ignored " + retired + " copies of retired bundled defaults." : "");
            }
            catch (Exception ex)
            {
                saved.Clear(); LoadStatus = "Overrides were not loaded: " + ex.Message;
            }
        }
        // Bundled values later corrected. Development calibration saves copied the whole
        // default into the personal file, so an untouched copy would otherwise keep
        // overriding the correction (the 0.9.34 fishing-rod roll stayed reversed remotely).
        private static readonly Grip[] retiredDefaults =
        {
            new Grip
            {
                Prefab = "Tool/FishingRod", Variant = "default", X = -0.0375534669f, Y = -0.0136026684f, Z = -0.270694464f,
                Pitch = 38.5322266f, Yaw = -11.8926392f, Roll = -21.131134f, TwoHanded = true,
                LeftX = 0.05181565f, LeftY = -0.01262827f, LeftZ = -0.147758245f, LeftPitch = 39.7449036f, LeftYaw = -53.6831055f, LeftRoll = 9.07016f,
                ItemX = -0.362749219f, ItemY = -0.3f, ItemZ = -0.265552f, ItemRoll = -166.365143f,
            },
        };
        internal static bool IsRetiredDefault(Grip grip)
        {
            static bool Near(float a, float b) => Mathf.Abs(a - b) <= 0.01f;
            foreach (var old in retiredDefaults)
                if (grip.Prefab == old.Prefab && grip.Variant == old.Variant && grip.TwoHanded == old.TwoHanded &&
                    Near(grip.X, old.X) && Near(grip.Y, old.Y) && Near(grip.Z, old.Z) &&
                    Near(grip.Pitch, old.Pitch) && Near(grip.Yaw, old.Yaw) && Near(grip.Roll, old.Roll) &&
                    Near(grip.LeftX, old.LeftX) && Near(grip.LeftY, old.LeftY) && Near(grip.LeftZ, old.LeftZ) &&
                    Near(grip.LeftPitch, old.LeftPitch) && Near(grip.LeftYaw, old.LeftYaw) && Near(grip.LeftRoll, old.LeftRoll) &&
                    Near(grip.ItemX, old.ItemX) && Near(grip.ItemY, old.ItemY) && Near(grip.ItemZ, old.ItemZ) &&
                    Near(grip.ItemPitch, old.ItemPitch) && Near(grip.ItemYaw, old.ItemYaw) && Near(grip.ItemRoll, old.ItemRoll))
                    return true;
            return false;
        }
        private static Dictionary<(string Prefab, string Variant), Grip> ReadDocument(string text)
        {
            var document = JsonConvert.DeserializeObject<Document>(text);
            if (document == null || document.Version != 1 || document.Items == null || document.Items.Count > 2048)
                throw new InvalidDataException("Unsupported held-grip document.");
            var result = new Dictionary<(string Prefab, string Variant), Grip>();
            foreach (var grip in document.Items)
            {
                Validate(grip);
                var key = Key(grip.Prefab, grip.Variant);
                if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate held-grip key.");
                result.Add(key, grip.Copy());
            }
            return result;
        }
        private static Dictionary<(string Prefab, string Variant), Grip> ReadDefaults()
        {
            // Assembly data is shared read-only; personal files contain overrides only.
            // Missing/broken presentation data must never prevent loading the mod.
            try
            {
                using var stream = typeof(HeldGripOverrides).Assembly.GetManifestResourceStream("MonMP.HeldGripDefaults.json");
                if (stream == null || stream.Length > 1024 * 1024) return new Dictionary<(string, string), Grip>();
                using var reader = new StreamReader(stream);
                return ReadDocument(reader.ReadToEnd());
            }
            catch (Exception) { return new Dictionary<(string, string), Grip>(); }
        }
        internal static string VariantOf(InteractableItems item) => item is Tool_AdjustableSpanner tool && tool.IntValue1_Max == 1 ? "spanner-second" : "default";
        internal static string IdentityOf(InteractableItems item) => sceneModels.TryGetValue(item, out var model) ? model.Key : item.PrefabName;
        internal static bool TryGetSceneModel(InteractableItems item, out string key, out string shape)
        {
            if (sceneModels.TryGetValue(item, out var model)) { key = model.Key; shape = model.Shape; return true; }
            key = ""; shape = ""; return false;
        }
        // Model identity, independent of station instance, root name, parent, sibling indices,
        // held pose, renderer enabled flags and active state. A hose nozzle remains the same
        // model after native equip reparents it under the player. No gameplay getters run here.
        internal static void RegisterSceneEquipment(InteractableTools item, out string key, out string shape)
        {
            if (TryGetSceneModel(item, out key, out shape)) return;
            var lines = new List<string>();
            void Visit(Transform node, string path)
            {
                if (node != item.transform && node.GetComponent<InteractableItems>() != null) return;
                foreach (var component in node.GetComponents<Component>())
                    lines.Add(path + "/component:" + (component == null ? "<missing>" : component.GetType().FullName));
                var filter = node.GetComponent<MeshFilter>();
                var skin = node.GetComponent<SkinnedMeshRenderer>();
                var mesh = filter != null ? filter.sharedMesh : skin != null ? skin.sharedMesh : null;
                if (mesh != null) lines.Add(path + "/mesh:" + mesh.name + "/" + mesh.vertexCount + "/" + mesh.subMeshCount);
                var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Transform child in node)
                {
                    ordinals.TryGetValue(child.name, out int ordinal); ordinals[child.name] = ordinal + 1;
                    Visit(child, path + "/" + child.name.Length + ":" + child.name + "[" + ordinal + "]");
                }
            }
            Visit(item.transform, "root"); lines.Sort(StringComparer.Ordinal);
            using var sha = SHA256.Create();
            shape = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "");
            key = "scene-equipment/" + item.GetType().Name + "/" + shape;
            sceneModels.Add(item, new SceneModel { Key = key, Shape = shape });
        }
        private static (string Prefab, string Variant) Key(string prefab, string variant) => (prefab, variant);
        private bool TryFind(string prefab, string variant, string shape, out Grip value)
        {
            var key = Key(prefab, variant);
            if (saved.TryGetValue(key, out value!) && value.Shape == shape) return true;
            if (defaults.TryGetValue(key, out value!) && value.Shape == shape) return true;
            value = null!; return false;
        }
        internal bool HasSaved(string prefab, string variant, string shape) =>
            saved.TryGetValue(Key(prefab, variant), out var value) && value.Shape == shape;
        // Read-only runtime loan; editor lookups continue returning detached drafts.
        internal bool TryGetNamedRuntime(string key, string shape, out Grip pose) => TryFind(key, "default", shape, out pose);
        internal bool TryGet(string prefab, string variant, string shape, out Grip grip)
        {
            if (TryFind(prefab, variant, shape, out var value))
            { grip = value.Copy(); return true; }
            grip = null!; return false;
        }
        internal bool TryGet(InteractableItems item, string shape, out Grip grip) => TryGet(IdentityOf(item), VariantOf(item), shape, out grip);
        // The chooser receives detached snapshots of validated saved calibrations.

#if MONMP_DEVELOPMENT
        internal IReadOnlyList<Grip> GetSavedGrips() => saved.Values.OrderBy(g => g.Prefab, StringComparer.Ordinal)
            .ThenBy(g => g.Variant, StringComparer.Ordinal).Select(g => g.Copy()).ToArray();
#endif


#if MONMP_DEVELOPMENT
        internal IReadOnlyList<Grip> GetCalibratedGrips() => saved.Values.Concat(defaults.Values.Where(g => !HasSaved(g.Prefab, g.Variant, g.Shape)))
            .OrderBy(g => g.Prefab, StringComparer.Ordinal).ThenBy(g => g.Variant, StringComparer.Ordinal)
            .ThenBy(g => g.Shape, StringComparer.Ordinal).Select(g => g.Copy()).ToArray();
#endif


#if MONMP_DEVELOPMENT
        internal bool TryCopyPose(string prefab, string variant, string shape, Grip destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!TryFind(prefab, variant, shape, out var source)) return false;
            destination.CopyPoseFrom(source);
            return true;
        }
#endif

        // The render loop receives value types, avoiding a mutable Grip copy per avatar/frame.
        // Defaults and local overrides share the exact identity/shape gate.
        internal bool TryGetRuntime(InteractableItems item, string shape, out Vector3 position, out Quaternion rotation, out float width)
            => TryGetRuntime(item, shape, out position, out rotation, out width, out _, out _);
        internal bool TryGetRuntime(InteractableItems item, string shape, out Vector3 position, out Quaternion rotation, out float width,
            out Vector3? leftPosition, out Quaternion? leftRotation)
        {
            position = default; rotation = default; width = 0;
            leftPosition = null; leftRotation = null;
            if (!TryFind(IdentityOf(item), VariantOf(item), shape, out var value)) return false;
            position = value.Position; rotation = Quaternion.Euler(value.Euler);
            if (value.TwoHanded) { leftPosition = value.LeftPosition; leftRotation = Quaternion.Euler(value.LeftEuler); }
            else width = value.Width;
            return true;
        }
        internal bool TryGetVisualOffset(InteractableItems item, string shape, out Vector3 bodyPosition, out Quaternion itemRotation)
        {
            bodyPosition = default; itemRotation = Quaternion.identity;
            if (!TryFind(IdentityOf(item), VariantOf(item), shape, out var value) || !value.HasItemOffset) return false;
            bodyPosition = value.ItemPosition; itemRotation = Quaternion.Euler(value.ItemEuler); return true;
        }
        // Writes a complete validated replacement before changing the in-memory active settings.
        // Existing settings retain a .bak rollback copy; failed writes leave active settings alone.

#if MONMP_DEVELOPMENT
        internal void Save(Grip grip)
        {
            Validate(grip);
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
            next[Key(grip.Prefab, grip.Variant)] = grip.Copy(); Write(next);
        }
#endif


#if MONMP_DEVELOPMENT
        internal void Remove(string prefab, string variant)
        {
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
            next.Remove(Key(prefab, variant)); Write(next);
        }
#endif


#if MONMP_DEVELOPMENT
        private void Write(Dictionary<(string Prefab, string Variant), Grip> next)
        {
            string directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Settings require a directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var document = new Document { Items = next.Values.OrderBy(g => g.Prefab, StringComparer.Ordinal).ThenBy(g => g.Variant, StringComparer.Ordinal).ToList() };
                File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented));
                if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak"); else File.Move(temporary, Path);
                saved.Clear(); foreach (var pair in next) saved.Add(pair.Key, pair.Value);
                LoadStatus = "Saved " + saved.Count + " overrides.";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
#endif

        internal static void Validate(Grip grip)
        {
            if (grip == null || string.IsNullOrWhiteSpace(grip.Prefab) || grip.Prefab.Length > 256 || grip.Prefab.Any(char.IsControl) ||
                (grip.Variant != "default" && grip.Variant != "spanner-second") || grip.Shape == null || grip.Shape.Length != 64 || !grip.Shape.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid held-grip identity.");
            foreach (float value in new[] { grip.X, grip.Y, grip.Z, grip.Pitch, grip.Yaw, grip.Roll, grip.Width,
                grip.LeftX, grip.LeftY, grip.LeftZ, grip.LeftPitch, grip.LeftYaw, grip.LeftRoll,
                grip.ItemX, grip.ItemY, grip.ItemZ, grip.ItemPitch, grip.ItemYaw, grip.ItemRoll })
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite held-grip value.");
            if (Math.Abs(grip.X) > 10 || Math.Abs(grip.Y) > 10 || Math.Abs(grip.Z) > 10 ||
                Math.Abs(grip.Pitch) > 360 || Math.Abs(grip.Yaw) > 360 || Math.Abs(grip.Roll) > 360 || grip.Width < 0 || grip.Width > 2 ||
                Math.Abs(grip.LeftX) > 10 || Math.Abs(grip.LeftY) > 10 || Math.Abs(grip.LeftZ) > 10 ||
                Math.Abs(grip.LeftPitch) > 360 || Math.Abs(grip.LeftYaw) > 360 || Math.Abs(grip.LeftRoll) > 360 ||
                Math.Abs(grip.ItemX) > 2 || Math.Abs(grip.ItemY) > 2 || Math.Abs(grip.ItemZ) > 2 ||
                Math.Abs(grip.ItemPitch) > 360 || Math.Abs(grip.ItemYaw) > 360 || Math.Abs(grip.ItemRoll) > 360)
                throw new InvalidDataException("Held-grip value outside supported range.");
        }
    }
}
