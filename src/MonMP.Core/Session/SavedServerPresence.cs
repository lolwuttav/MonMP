using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace MonMP.Core.Session
{
    /// <summary>Matches a saved direct address to a host that answered a recent LAN discovery query.</summary>
    public static class SavedServerPresence
    {
        public static bool Matches(DirectHostEndpoint saved, IPAddress discoveredAddress, int discoveredPort,
            IEnumerable<IPAddress>? resolvedAddresses = null)
        {
            if (saved.Port != discoveredPort) return false;
            if (IPAddress.TryParse(saved.Host, out var literal)) return literal.Equals(discoveredAddress);
            return resolvedAddresses?.Any(address => address.Equals(discoveredAddress)) == true;
        }

        public static string FormatTag(bool discovered, int players, bool passwordRequired)
        {
            if (!discovered) return "Status unknown";
            return players + (players == 1 ? " player" : " players") + (passwordRequired ? "  |  password" : "");
        }
    }
}
