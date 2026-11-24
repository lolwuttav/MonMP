using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace MonMP.Core.World
{
    [Flags]
    public enum PrefabFeatures : byte { Registered = 0, Carry = 1, Consume = 2, Values = 4, Interact = 8 }

    public sealed class PrefabDefinition
    {
        public ushort Id { get; }
        public string Path { get; }
        public string TypeName { get; }
        public string ShapeHash { get; }
        public PrefabFeatures Features { get; }
        public PrefabDefinition(ushort id, string path, string typeName, string shapeHash, PrefabFeatures features)
        {
            if (id == 0 || string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 256 ||
                path.Any(c => char.IsControl(c) || c == '\\' || c == ':') || path.Split('/').Any(p => p.Length == 0 || p == "." || p == "..") ||
                string.IsNullOrWhiteSpace(typeName) || Encoding.UTF8.GetByteCount(typeName) > 128 || typeName.Any(char.IsControl) ||
                shapeHash == null || shapeHash.Length != 64 || shapeHash.Any(c => !Uri.IsHexDigit(c)) || ((byte)features & ~15) != 0 ||
                ((features & PrefabFeatures.Consume) != 0 && (features & PrefabFeatures.Carry) == 0))
                throw new InvalidDataException("Invalid prefab catalog definition.");
            Id = id; Path = path; TypeName = typeName; ShapeHash = shapeHash.ToUpperInvariant(); Features = features;
        }
        public WorldRecord Descriptor()
        {
            // Include local layout and adapter capabilities in the static identity,
            // so the handshake cannot accept a path with a different factory shape.
            string key = "item-prefab-v2/" + Id + "/" + Path.Length + ":" + Path + "/" + TypeName.Length + ":" + TypeName + "/" + ShapeHash + "/" + (byte)Features;
            return new WorldRecord(StaticId.Hash(key), 1, 3, Array.Empty<byte>());
        }
    }

    public sealed class PrefabCatalog
    {
        public IReadOnlyDictionary<ushort, PrefabDefinition> Entries { get; }
        public string Hash { get; }
        public PrefabCatalog(IEnumerable<PrefabDefinition> definitions)
        {
            var entries = new Dictionary<ushort, PrefabDefinition>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var descriptors = new HashSet<ulong>();
            foreach (var definition in definitions)
            {
                if (entries.ContainsKey(definition.Id) || !paths.Add(definition.Path) || !descriptors.Add(definition.Descriptor().Id))
                    throw new InvalidDataException("Duplicate prefab ID, resource path or descriptor identity.");
                entries.Add(definition.Id, definition);
            }
            if (entries.Count == 0) throw new InvalidDataException("Empty prefab catalog.");
            Entries = new ReadOnlyDictionary<ushort, PrefabDefinition>(entries);
            Hash = WorldRecord.Digest(entries.Values.Select(e => e.Descriptor()), true);
        }
    }
}
