using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    internal static class Barrel120AuthorityAdapter
    {
        internal static bool MayRunNativeStoreReceive()
        {
            var session = MonMPPlugin.Instance?.Session;
            return Barrel120AuthorityPolicy.MayRunNativeStoreReceive(
                session?.IsRunning == true,
                session?.IsHost == true);
        }
    }

    // Both native buyback methods pay local cash and destroy the barrel. Until those
    // transactions are routed through the host, a guest must not run either mutation.
    [HarmonyPatch(typeof(Store_FederationSyrup), nameof(Store_FederationSyrup.Receive), new[] { typeof(Item_Barrel120) })]
    internal static class FederationSyrupBarrelAuthorityPatch
    {
        private static bool Prefix() => Barrel120AuthorityAdapter.MayRunNativeStoreReceive();
    }

    [HarmonyPatch(typeof(Store_IslandHouses), nameof(Store_IslandHouses.Receive), new[] { typeof(Item_Barrel120) })]
    internal static class IslandHousesBarrelAuthorityPatch
    {
        private static bool Prefix() => Barrel120AuthorityAdapter.MayRunNativeStoreReceive();
    }
}
