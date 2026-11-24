using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Hand-defined cargo compartments per carrier model, in the carrier frame's local space.
    // A model with boxes uses only those boxes as its freeze zones; other models keep the
    // measured floor/wall cavity. Bundled defaults ship with the mod; the personal file
    // (written by the development editor) overrides a model's whole box list.
    internal sealed class CargoBoundsOverrides
    {
        private const int MaximumBytes = 1024 * 1024, MaximumVehicles = 256;
        internal const int MaximumBoxes = 16;

        internal sealed class Box
        {
            public float X, Y, Z, Width = 1, Height = 1, Length = 1;
            internal Bounds Bounds => new Bounds(new Vector3(X, Y, Z), new Vector3(Width, Height, Length));
            internal Box Copy() => (Box)MemberwiseClone();
        }

        internal sealed class Entry
        {
            [JsonProperty(Required = Required.Always)] public string Vehicle = "";
            [JsonProperty(Required = Required.Always)] public List<Box> Boxes = new List<Box>();
            internal Entry Copy() => new Entry { Vehicle = Vehicle, Boxes = Boxes.Select(b => b.Copy()).ToList() };
        }

        private sealed class Document
        {
            [JsonProperty(Required = Required.Always)] public int Version = 1;
            [JsonProperty(Required = Required.Always)] public List<Entry> Vehicles = new List<Entry>();
        }

        private static readonly Dictionary<string, Entry> defaults = ReadDefaults();
        private readonly Dictionary<string, Entry> saved = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Bounds[]> runtime = new Dictionary<string, Bounds[]>(StringComparer.Ordinal);
        internal string Path { get; }
        internal string LoadStatus { get; private set; } = "No saved cargo bounds.";
        internal int Count => saved.Count;
        internal static int DefaultCount => defaults.Count;

        internal CargoBoundsOverrides(string path)
        {
            Path = path;
            if (!File.Exists(path)) return;
            try
            {
                if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Cargo-bounds file exceeds 1 MiB.");
                foreach (var pair in ReadDocument(File.ReadAllText(path))) saved.Add(pair.Key, pair.Value);
                LoadStatus = "Loaded cargo bounds for " + saved.Count + " vehicles.";
            }
            catch (Exception ex) { saved.Clear(); LoadStatus = "Cargo bounds were not loaded: " + ex.Message; }
        }

        private static Dictionary<string, Entry> ReadDefaults()
        {
            // Missing/broken shared defaults must never prevent loading the mod.
            try
            {
                using var stream = typeof(CargoBoundsOverrides).Assembly.GetManifestResourceStream("MonMP.CargoBoundsDefaults.json");
                if (stream == null || stream.Length > MaximumBytes) return new Dictionary<string, Entry>(StringComparer.Ordinal);
                using var reader = new StreamReader(stream);
                return ReadDocument(reader.ReadToEnd());
            }
            catch (Exception) { return new Dictionary<string, Entry>(StringComparer.Ordinal); }
        }

        private static Dictionary<string, Entry> ReadDocument(string text)
        {
            var document = JsonConvert.DeserializeObject<Document>(text, new JsonSerializerSettings { MaxDepth = 8, CheckAdditionalContent = true });
            if (document == null || document.Version != 1 || document.Vehicles == null || document.Vehicles.Count > MaximumVehicles)
                throw new InvalidDataException("Unsupported cargo-bounds document.");
            var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var entry in document.Vehicles)
            {
                Validate(entry);
                if (result.ContainsKey(entry.Vehicle)) throw new InvalidDataException("Duplicate cargo-bounds vehicle.");
                result.Add(entry.Vehicle, entry.Copy());
            }
            return result;
        }

        internal static void Validate(Entry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Vehicle) || entry.Vehicle.Length > 256 || entry.Vehicle.Any(char.IsControl))
                throw new InvalidDataException("Invalid cargo-bounds vehicle.");
            if (entry.Boxes == null || entry.Boxes.Count == 0 || entry.Boxes.Count > MaximumBoxes)
                throw new InvalidDataException("A cargo-bounds vehicle needs 1 to " + MaximumBoxes + " boxes.");
            foreach (var box in entry.Boxes)
            {
                if (box == null) throw new InvalidDataException("Missing cargo box.");
                foreach (float value in new[] { box.X, box.Y, box.Z })
                    if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 20f) throw new InvalidDataException("Cargo box position outside supported range.");
                foreach (float value in new[] { box.Width, box.Height, box.Length })
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0.05f || value > 20f) throw new InvalidDataException("Cargo box size outside supported range.");
            }
        }

        // Carrier frames are vehicle/trailer/boat roots. Spawned copies share the model name.
        internal static string ModelKey(Transform frame)
        {
            string name = frame.gameObject.name.Replace("(Clone)", "").Trim();
            var vehicle = frame.GetComponent<Vehicle>();
            return vehicle != null ? "vehicle/" + vehicle.vehicle_type + "/" + name : "carrier/" + name;
        }

        private bool TryFind(string key, out Entry entry) => saved.TryGetValue(key, out entry!) || defaults.TryGetValue(key, out entry!);

        // Borrowed for the drop path; callers must not modify the array.
        internal bool TryGetRuntime(Transform frame, out Bounds[] boxes)
        {
            string key = ModelKey(frame);
            if (runtime.TryGetValue(key, out boxes!)) return boxes.Length > 0;
            boxes = TryFind(key, out var entry) ? entry.Boxes.Select(b => b.Bounds).ToArray() : Array.Empty<Bounds>();
            if (runtime.Count >= MaximumVehicles * 2) runtime.Clear();
            runtime[key] = boxes;
            return boxes.Length > 0;
        }

        internal bool HasSaved(string key) => saved.ContainsKey(key);
        internal bool HasDefault(string key) => defaults.ContainsKey(key);
        internal Entry? GetCopy(string key) => TryFind(key, out var entry) ? entry.Copy() : null;

#if MONMP_DEVELOPMENT
        internal void Save(Entry entry)
        {
            Validate(entry);
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
            next[entry.Vehicle] = entry.Copy(); Write(next);
        }

        internal void Remove(string key)
        {
            if (!saved.ContainsKey(key)) return;
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
            next.Remove(key); Write(next);
        }

        // Validated, bounded replacement; a .bak keeps the previous file and a failed write keeps the cache.
        private void Write(Dictionary<string, Entry> next)
        {
            if (next.Count > MaximumVehicles) throw new InvalidDataException("Too many cargo-bounds vehicles.");
            var document = new Document { Vehicles = next.Values.OrderBy(e => e.Vehicle, StringComparer.Ordinal).ToList() };
            string text = JsonConvert.SerializeObject(document, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Cargo-bounds file exceeds 1 MiB.");
            string directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Settings require a directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak"); else File.Move(temporary, Path);
                saved.Clear(); foreach (var pair in next) saved.Add(pair.Key, pair.Value);
                runtime.Clear();
                LoadStatus = "Saved cargo bounds for " + saved.Count + " vehicles.";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
#endif
    }
}
