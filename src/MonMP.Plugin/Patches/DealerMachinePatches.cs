using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(ItemForSale_Dealer), nameof(ItemForSale_Dealer.MouseDown))]
    internal static class DealerMachinePurchasePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        { yield return AccessTools.Method(typeof(ItemForSale_Dealer), nameof(ItemForSale_Dealer.MouseDown))!; }

        private static bool Prefix(ItemForSale_Dealer __instance)
        {
            var adapter = SharedDealerMachines.Active;
            if (adapter?.InterceptMouseDown(__instance) != true) return true;
            __instance.MouseOut();
            return false;
        }

        private static void Postfix(ItemForSale_Dealer __instance) =>
            SharedDealerMachines.Active?.AfterNativeMouseDown(__instance);
    }

    // The native hover colors the dealer's price by isCanAfford, which a guest holds false to block
    // local purchases: every price showed unaffordable. Quote it from the shared wallet (#216).
    [HarmonyPatch(typeof(ItemForSale_Dealer), nameof(ItemForSale_Dealer.MouseHover))]
    internal static class DealerPriceQuotePatch
    {
        [System.ThreadStatic] internal static bool Quoting;
        private static void Prefix() => Quoting = true;
        private static void Finalizer() => Quoting = false;
    }
}
