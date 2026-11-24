using System;
using System.Net;
using System.Net.Sockets;

namespace MonMP.Core.Transport
{
    public static class ExternalAddressScope
    {
        // RFC 1918 and RFC 6598: an address reported by a local gateway in one
        // of these ranges cannot establish direct public IPv4 reachability.
        public static bool IsPrivateOrSharedIPv4(IPAddress address)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
    }
}
