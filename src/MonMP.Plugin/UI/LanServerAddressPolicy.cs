using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace MonMP.Plugin.UI
{
    internal readonly struct LanSubnet
    {
        internal readonly IPAddress Address, Mask;
        internal readonly bool Physical, Gateway;
        internal LanSubnet(IPAddress address, IPAddress mask, bool physical, bool gateway)
        { Address = address; Mask = mask; Physical = physical; Gateway = gateway; }
    }

    // Rank only addresses actually observed from discovery replies. Private address ranges
    // do not identify virtual adapters: a real LAN may legitimately use 172.16/12.
    internal static class LanServerAddressPolicy
    {
        internal static int Rank(IPAddress address, IEnumerable<LanSubnet> subnets)
        {
            if (IPAddress.IsLoopback(address)) return 0;
            int rank = 6;
            if (address.AddressFamily != AddressFamily.InterNetwork) return rank;
            var remote = address.GetAddressBytes();
            foreach (var subnet in subnets)
            {
                if (subnet.Address.AddressFamily != AddressFamily.InterNetwork ||
                    subnet.Mask.AddressFamily != AddressFamily.InterNetwork) continue;
                var local = subnet.Address.GetAddressBytes(); var mask = subnet.Mask.GetAddressBytes();
                bool same = true, hasMask = false;
                for (int i = 0; i < 4; i++)
                {
                    hasMask |= mask[i] != 0;
                    same &= (remote[i] & mask[i]) == (local[i] & mask[i]);
                }
                if (!same || !hasMask) continue;
                rank = Math.Min(rank, subnet.Physical ? (subnet.Gateway ? 1 : 2) : (subnet.Gateway ? 3 : 5));
            }
            return rank;
        }

        internal static bool IsPhysical(string name, string description, bool ethernetOrWifi)
        {
            if (!ethernetOrWifi) return false;
            string text = name + " " + description;
            foreach (string marker in new[] { "vEthernet", "Hyper-V", "Virtual", "VMware", "VPN", "WireGuard", "TAP-", "TUN-" })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
    }
}

