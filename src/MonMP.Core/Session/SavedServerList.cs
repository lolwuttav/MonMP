using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MonMP.Core.Session
{
    /// <summary>A named direct server the player saved in the multiplayer menu. No password is stored.</summary>
    public sealed class SavedServer : IEquatable<SavedServer>
    {
        public const int MaxNameBytes = 48;
        public string Name { get; }
        public DirectHostEndpoint Endpoint { get; }
        public SavedServer(string name, DirectHostEndpoint endpoint) { Name = CleanName(name, endpoint.Display); Endpoint = endpoint; }

        // The config separators are removed; an empty name falls back to the address.
        public static string CleanName(string? name, string fallback)
        {
            var text = new StringBuilder();
            string source = (name ?? "").Trim();
            int bytes = 0;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '|' || c == ';' || char.IsControl(c)) continue;
                // Whole characters only: a surrogate pair is kept or dropped together, never split at
                // the byte cap, and a lone surrogate is dropped.
                bool pair = char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]);
                if (!pair && char.IsSurrogate(c)) continue;
                int size = pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                if (bytes + size > MaxNameBytes) break;
                bytes += size;
                text.Append(c);
                if (pair) text.Append(source[++i]);
            }
            string cleaned = text.ToString().Trim();
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        // A typed server address: host, host:port, [IPv6]:port or a bare IPv6 address.
        public static bool TryParseAddress(string? text, int defaultPort, out DirectHostEndpoint endpoint)
        {
            endpoint = default;
            string value = (text ?? "").Trim();
            if (value.Length == 0) return false;
            string host = value; int port = defaultPort;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int close = value.IndexOf(']');
                if (close < 0) return false;
                host = value.Substring(1, close - 1);
                string rest = value.Substring(close + 1);
                if (rest.Length != 0 && (!rest.StartsWith(":", StringComparison.Ordinal) ||
                    !int.TryParse(rest.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out port))) return false;
            }
            else if (value.IndexOf(':') == value.LastIndexOf(':') && value.IndexOf(':') > 0)
            {
                int colon = value.IndexOf(':');
                host = value.Substring(0, colon);
                if (!int.TryParse(value.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            }
            return DirectHostEndpoint.TryCreate(host, port, out endpoint);
        }

        public bool Equals(SavedServer? other) => other != null && Name == other.Name && Endpoint == other.Endpoint;
        public override bool Equals(object? obj) => obj is SavedServer other && Equals(other);
        public override int GetHashCode() => Name.GetHashCode() ^ Endpoint.GetHashCode();
    }

    /// <summary>The multiplayer menu's server list, in the player's order. Config format: name|host|port;...</summary>
    public sealed class SavedServerList
    {
        public const int MaxEntries = 32;
        // A blank label uses host:port and can exceed MaxNameBytes. Thirty-two valid
        // 253-character DNS names plus those fallback labels need up to 16,639 chars.
        // Keep the local config read bounded while admitting every list we can write.
        public const int MaxSerializedLength = 32768;
        private readonly ReadOnlyCollection<SavedServer> entries;
        public IReadOnlyList<SavedServer> Entries => entries;
        public static SavedServerList Empty { get; } = new SavedServerList(Array.Empty<SavedServer>());

        private SavedServerList(IEnumerable<SavedServer> values) => entries = Array.AsReadOnly(values.Take(MaxEntries).ToArray());

        public static SavedServerList Parse(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized) || serialized!.Length > MaxSerializedLength) return Empty;
            var parsed = new List<SavedServer>();
            foreach (string item in serialized.Split(';'))
            {
                if (parsed.Count == MaxEntries) break;
                string[] parts = item.Split('|');
                if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                    !DirectHostEndpoint.TryCreate(parts[1], port, out var endpoint)) continue;
                // The automatically generated address label is not an explicit user name.
                // Restore it through the same fallback path rather than truncating it on reload.
                parsed.Add(new SavedServer(parts[0] == endpoint.Display ? "" : parts[0], endpoint));
            }
            return new SavedServerList(parsed);
        }

        public string Serialize() => string.Join(";", entries.Select(server =>
            server.Name + "|" + server.Endpoint.Host + "|" + server.Endpoint.Port.ToString(CultureInfo.InvariantCulture)));

        public SavedServerList Add(SavedServer server)
        {
            if (entries.Count >= MaxEntries) throw new InvalidOperationException("The server list is full.");
            return new SavedServerList(entries.Append(server));
        }

        public SavedServerList Replace(int index, SavedServer server)
        {
            if (index < 0 || index >= entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return new SavedServerList(entries.Select((value, i) => i == index ? server : value));
        }

        public SavedServerList Remove(int index)
        {
            if (index < 0 || index >= entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return new SavedServerList(entries.Where((_, i) => i != index));
        }

        public SavedServerList Move(int index, int offset)
        {
            int target = index + offset;
            if (index < 0 || index >= entries.Count || target < 0 || target >= entries.Count) return this;
            var list = entries.ToList();
            var item = list[index]; list.RemoveAt(index); list.Insert(target, item);
            return new SavedServerList(list);
        }
    }
}
