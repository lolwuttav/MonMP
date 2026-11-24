using System;
using System.Globalization;
using System.Linq;
using System.Net;

namespace MonMP.Core.Session
{
    /// <summary>A validated direct host and UDP port. Passwords are deliberately not part of this value.</summary>
    public readonly struct DirectHostEndpoint : IEquatable<DirectHostEndpoint>
    {
        public string Host { get; }
        public int Port { get; }
        public bool IsIPv6 => IPAddress.TryParse(Host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        public string Display => IsIPv6 ? "[" + Host + "]:" + Port.ToString(CultureInfo.InvariantCulture) : Host + ":" + Port.ToString(CultureInfo.InvariantCulture);

        private DirectHostEndpoint(string host, int port) { Host = host; Port = port; }

        public static bool TryCreate(string? host, int port, out DirectHostEndpoint endpoint)
        {
            endpoint = default;
            if (port < 1 || port > 65535 || string.IsNullOrWhiteSpace(host)) return false;
            string value = host.Trim();
            if (value.Length == 0 || value.Length > 512 || value.Any(char.IsWhiteSpace)) return false;

            bool bracketed = value[0] == '[' || value[value.Length - 1] == ']';
            if (bracketed)
            {
                if (value.Length < 3 || value[0] != '[' || value[value.Length - 1] != ']') return false;
                value = value.Substring(1, value.Length - 2);
                if (!IPAddress.TryParse(value, out var bracketedAddress) || bracketedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return false;
                endpoint = new DirectHostEndpoint(bracketedAddress.ToString(), port);
                return true;
            }

            if (IPAddress.TryParse(value, out var address))
            {
                endpoint = new DirectHostEndpoint(address.ToString(), port);
                return true;
            }

            // A hostname follows DNS label rules. Convert international names to their stable ASCII form
            // before storing them so comparison and persisted values are deterministic.
            if (value[value.Length - 1] == '.') value = value.Substring(0, value.Length - 1);
            if (value.Length == 0) return false;
            string ascii;
            try { ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value); }
            catch (ArgumentException) { return false; }
            if (ascii.Length > 253) return false;
            string[] labels = ascii.Split('.');
            if (labels.Any(label => label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-' ||
                label.Any(character => !(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                    !(character >= '0' && character <= '9') && character != '-'))) return false;
            if (labels.Length > 1 && labels.All(label => label.All(char.IsDigit))) return false;
            endpoint = new DirectHostEndpoint(ascii.ToLowerInvariant(), port);
            return true;
        }

        public bool Equals(DirectHostEndpoint other) => Port == other.Port && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => obj is DirectHostEndpoint other && Equals(other);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Host ?? string.Empty) * 397 ^ Port;
        public static bool operator ==(DirectHostEndpoint left, DirectHostEndpoint right) => left.Equals(right);
        public static bool operator !=(DirectHostEndpoint left, DirectHostEndpoint right) => !left.Equals(right);
    }
}
