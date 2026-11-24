using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public sealed class InteractionCoverage
    {
        private const string Header = "type\tcategory\tdisposition\tadapter\texecution\tlateJoin\ttests\tscope";
        public IReadOnlyDictionary<string, string[]> Entries { get; }
        public int PendingCount => Entries.Values.Count(v => v[2] == "pending");
        public InteractionCoverage(string text)
        {
            var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
            using var reader = new StringReader(text.TrimStart('\uFEFF'));
            if (reader.ReadLine() != Header) throw new InvalidDataException("Unknown coverage schema.");
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var row = line.Split('\t');
                if (row.Length != 8 || row.Any(string.IsNullOrWhiteSpace) || entries.ContainsKey(row[0]) ||
                    (row[1] == "session" && !row[0].StartsWith("MonMP.", StringComparison.Ordinal)) ||
                    !new[] { "pending", "partial", "supported", "local", "presentation" }.Contains(row[2]) ||
                    !new[] { "no", "scoped", "yes" }.Contains(row[5]) ||
                    (row[2] == "supported" && (row[3] == "-" || row[4] == "-" || row[6] == "pending")))
                    throw new InvalidDataException("Invalid or duplicate interaction coverage row.");
                entries.Add(row[0], row);
            }
            if (entries.Count == 0) throw new InvalidDataException("Empty interaction coverage.");
            Entries = entries;
        }
        public void ValidateTypes(IEnumerable<string> concreteTypes, IEnumerable<string>? nativeSystems = null)
        {
            var types = new HashSet<string>(concreteTypes, StringComparer.Ordinal);
            // Session UI and explicitly classified native components are supplemental rows;
            // every other row must still match the complete concrete Interactable inventory.
            var native = Entries.Where(e => e.Value[1] != "session" && e.Value[1] != "native-system").Select(e => e.Key).ToArray();
            if (!types.SetEquals(native)) throw new InvalidDataException("Interaction coverage does not match the gameplay assembly: " +
                string.Join(", ", types.Except(native).Concat(native.Except(types)).Take(10)));
            var systems = new HashSet<string>(nativeSystems ?? Array.Empty<string>(), StringComparer.Ordinal);
            var missing = Entries.Where(e => e.Value[1] == "native-system" && (!systems.Contains(e.Key) || types.Contains(e.Key))).Select(e => e.Key).ToArray();
            if (missing.Length != 0) throw new InvalidDataException("Native system coverage does not match the gameplay assembly: " +
                string.Join(", ", missing.Take(10)));
        }
    }
}
