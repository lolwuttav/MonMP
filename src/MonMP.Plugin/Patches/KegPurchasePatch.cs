using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // ItemForSale_Kegs checks local GameManager friendship before the common store path. Always
    // route multiplayer clicks first so a stale guest friendship value cannot reject a valid host buy.
    [HarmonyPatch(typeof(ItemForSale_Kegs), nameof(ItemForSale_Kegs.MouseDown))]
    internal static class KegPurchasePatch
    {
        private static bool Prefix(ItemForSale_Kegs __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            // The host owns the native cash/economy path. Only guests need the custom request route.
            if (plugin.Session.IsHost) return true;
            if (SharedKegPurchase.Active?.IsExecutingNative(__instance) == true) return true;

            if (SharedKegPurchase.Active?.Purchase(__instance) == true)
            {
                __instance.MouseOut();
                return false;
            }

            if (!plugin.Session.IsHost)
                plugin.ReportItemUseUnavailable("Keg purchases are unavailable until the host world is synchronized.");
            return false;
        }
    }
}
