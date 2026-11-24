using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MonMP.Core.World
{
    public static class StaticId
    {
        // Length prefixes prevent ambiguous paths when Unity names contain separators.
        public static string Segment(string name, int sibling) => name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name + "[" + sibling.ToString(CultureInfo.InvariantCulture) + "]";

        public static ulong Hash(string canonicalPath)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(canonicalPath)) hash = unchecked((hash ^ b) * 1099511628211UL);
            return hash;
        }
    }

    public sealed class IdentityRegistry
    {
        private readonly Dictionary<ulong, string> paths = new Dictionary<ulong, string>();
        public IReadOnlyDictionary<ulong, string> Paths => paths;
        public ulong Register(string path)
        {
            ulong id = StaticId.Hash(path);
            if (paths.TryGetValue(id, out var existing))
                throw new InvalidOperationException(existing == path ? "Duplicate identity: " + path : "Static ID collision: " + existing + " and " + path);
            paths.Add(id, path);
            return id;
        }
    }

    public static class StaticIdentityResolver
    {
        // A path is usable only when every observation of it refers to the same runtime
        // object. Distinct objects at one path are excluded together rather than selected
        // by enumeration order or aliased to one network record.
        public static Dictionary<string, T> UniqueByPath<T>(IEnumerable<KeyValuePair<string, T>> candidates,
            out string[] ambiguousPaths) where T : class
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            var unique = new Dictionary<string, T>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Key) || candidate.Value == null)
                    throw new ArgumentException("Static identity candidates require a path and object.", nameof(candidates));
                if (ambiguous.Contains(candidate.Key)) continue;
                if (!unique.TryGetValue(candidate.Key, out var existing))
                {
                    unique.Add(candidate.Key, candidate.Value);
                    continue;
                }
                if (!ReferenceEquals(existing, candidate.Value))
                {
                    unique.Remove(candidate.Key);
                    ambiguous.Add(candidate.Key);
                }
            }
            ambiguousPaths = ambiguous.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            return unique;
        }
    }
}
