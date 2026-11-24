using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(ItemForSale_BarDrink), nameof(ItemForSale_BarDrink.MouseHover))]
    internal static class SharedBarDrinkPriceHoverPatch
    {
        private static void Prefix(ItemForSale_BarDrink __instance, out ItemForSale_BarDrink? __state)
        {
            var adapter = SharedBarDrinkPurchase.Active;
            __state = adapter?.HoverListing;
            if (adapter != null) adapter.HoverListing = __instance;
        }

        private static void Finalizer(ItemForSale_BarDrink? __state)
        {
            var adapter = SharedBarDrinkPurchase.Active;
            if (adapter != null) adapter.HoverListing = __state;
        }
    }

    [HarmonyPatch(typeof(ItemForSale_BarDrink), nameof(ItemForSale_BarDrink.MouseDown))]
    internal static class SharedBarDrinkMouseDownPatch
    {
        private static bool Prefix(ItemForSale_BarDrink __instance) =>
            !(SharedBarDrinkPurchase.Active?.InterceptClick(__instance) ?? false);

        private static Exception? Finalizer(ItemForSale_BarDrink __instance, Exception? __exception)
        {
            SharedBarDrinkPurchase.Active?.OnNativeHostClick(__instance);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Store_Bar), nameof(Store_Bar.OpenStore))]
    internal static class SharedBarDrinkStoreOpenPatch
    {
        private static void Postfix(Store_Bar __instance) => SharedBarDrinkPurchase.Active?.OnStoreLifecycle(__instance);
    }

    [HarmonyPatch(typeof(Store_Bar), nameof(Store_Bar.CloseStore))]
    internal static class SharedBarDrinkStoreClosePatch
    {
        private static void Postfix(Store_Bar __instance) => SharedBarDrinkPurchase.Active?.OnStoreLifecycle(__instance);
    }

    // A remotely purchased drink is played locally for the guest's personal needs. Carole's
    // bottle response is shared progression and is therefore emitted by the host only.
    [HarmonyPatch(typeof(Actor_Carole), nameof(Actor_Carole.PlayerNewWineBottle))]
    internal static class SharedBarDrinkCarolePatch
    {
        private static bool Prefix() => SharedBarDrinkPurchase.Active?.SuppressLocalCaroleResponse != true;
    }
}
