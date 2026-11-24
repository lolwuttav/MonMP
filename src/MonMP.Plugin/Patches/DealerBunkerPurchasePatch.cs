using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // The native sign spends shared cash and starts construction. Guest clicks become a
    // small host request; only the host executes Store_Dealer.TryToBuyBunker.
    [HarmonyPatch(typeof(Store_Dealer_BunkerSellSign), nameof(Store_Dealer_BunkerSellSign.MouseDown))]
    internal static class DealerBunkerSellSignPatch
    {
        private static bool Prefix(Store_Dealer_BunkerSellSign __instance)
        {
            var adapter = SharedDealerBunkerPurchase.Active;
            if (adapter == null && MonMPPlugin.Instance?.Session?.IsRunning == true)
            {
                MonMPPlugin.Instance.ReportItemUseUnavailable("The bunker purchase is unavailable until host world synchronization is ready.");
                __instance.MouseOut();
                return false;
            }
            if (adapter?.Purchase(__instance) == true)
            {
                __instance.MouseOut();
                return false;
            }
            if (!SharedDealerBunkerPurchase.BlocksNativeGuestOperation) return true;
            MonMPPlugin.Instance?.ReportItemUseUnavailable("The bunker purchase is unavailable until host world synchronization is ready.");
            __instance.MouseOut();
            return false;
        }
    }

    [HarmonyPatch(typeof(Store_Dealer), nameof(Store_Dealer.TryToBuyBunker))]
    internal static class DealerBunkerNativePurchaseGuardPatch
    {
        private static bool Prefix() => !SharedDealerBunkerPurchase.BlocksNativeGuestOperation;
    }

    [HarmonyPatch(typeof(Home), nameof(Home.BunkerBought))]
    internal static class DealerBunkerBoughtGuardPatch
    {
        private static bool Prefix() => !SharedDealerBunkerPurchase.BlocksNativeGuestOperation;
    }

    [HarmonyPatch(typeof(Home), "NewDay", new[] { typeof(int) })]
    internal static class DealerBunkerDayGuardPatch
    {
        private static bool Prefix() => !SharedDealerBunkerPurchase.BlocksNativeGuestOperation;
    }

    [HarmonyPatch(typeof(Home), nameof(Home.UpdateBunkerConstruction))]
    internal static class DealerBunkerConstructionGuardPatch
    {
        private static bool Prefix() => !SharedDealerBunkerPurchase.BlocksNativeGuestOperation;
    }
}
