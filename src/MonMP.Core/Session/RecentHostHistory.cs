using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace MonMP.Core.Session
{
    /// <summary>Small, immutable history of validated direct endpoints. It has no password field or serialization path.</summary>
    public sealed class RecentHostHistory
    {
        public const int MaxEntries = 8;
        // Eight 253-character DNS names, separators and five-digit UDP ports.
        public const int MaxSerializedLength = MaxEntries * (253 + 1 + 5) + MaxEntries - 1;
        private readonly ReadOnlyCollection<DirectHostEndpoint> entries;
        public IReadOnlyList<DirectHostEndpoint> Entries => entries;

        private RecentHostHistory(IEnumerable<DirectHostEndpoint> values) => entries = Array.AsReadOnly(values.Take(MaxEntries).ToArray());

        public static RecentHostHistory Empty { get; } = new RecentHostHistory(Array.Empty<DirectHostEndpoint>());

        // Config format: host|port;host|port. The host validator rejects the separators.
        public static RecentHostHistory Parse(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized) || serialized.Length > MaxSerializedLength) return Empty;
            var parsed = new List<DirectHostEndpoint>();
            foreach (string item in serialized.Split(';'))
            {
                if (parsed.Count == MaxEntries) break;
                int separator = item.LastIndexOf('|');
                if (separator <= 0 || separator == item.Length - 1) continue;
                string host = item.Substring(0, separator);
                if (!int.TryParse(item.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                    !DirectHostEndpoint.TryCreate(host, port, out var endpoint) || parsed.Contains(endpoint)) continue;
                parsed.Add(endpoint);
            }
            return parsed.Count == 0 ? Empty : new RecentHostHistory(parsed);
        }

        public RecentHostHistory Remember(string host, int port)
        {
            if (!DirectHostEndpoint.TryCreate(host, port, out var endpoint)) throw new ArgumentException("Invalid direct host endpoint.");
            var next = new List<DirectHostEndpoint>(MaxEntries) { endpoint };
            next.AddRange(entries.Where(value => value != endpoint).Take(MaxEntries - 1));
            return new RecentHostHistory(next);
        }

        public string Serialize() => string.Join(";", entries.Select(endpoint => endpoint.Host + "|" + endpoint.Port.ToString(CultureInfo.InvariantCulture)));
    }
}
