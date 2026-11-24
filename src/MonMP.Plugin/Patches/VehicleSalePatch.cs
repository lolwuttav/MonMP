using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Guest clicks never run the native purchase locally. Host clicks keep the native path, with
    // a narrowly scoped context so only a registered listing can debit or spawn during a session.
    [HarmonyPatch(typeof(VehicleForSale_Display), nameof(VehicleForSale_Display.MouseDown))]
    internal static class VehicleSaleDisplayPatch
    {
        private static bool Prefix(VehicleForSale_Display __instance, out bool __state)
        {
            __state = false;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            var sales = SharedVehicleSales.Active;
            if (sales == null)
            {
                plugin.ReportItemUseUnavailable("Vehicle purchases are unavailable until the multiplayer world is initialized.");
                return false;
            }
            bool consumed = sales.InterceptMouseDown(__instance, out __state);
            if (consumed)
            {
                __instance.MouseOut();
                return false;
            }
            return true;
        }

        private static Exception? Finalizer(VehicleForSale_Display __instance, bool __state, Exception? __exception)
        {
            if (__state) SharedVehicleSales.Active?.FinishLocalClick(__instance, __exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Cash_Manager), nameof(Cash_Manager.TryToBuy_Vehicle))]
    internal static class VehicleSaleNativeTransactionPatch
    {
        private static bool Prefix(VehicleForSale_Display _vehicleForSale)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return SharedVehicleSales.Active?.IsExecutingNative(_vehicleForSale) == true;
        }
    }

    // MouseHover calls Cash_Manager.isCanAfford solely to choose the displayed offer color.
    // Scope the shared-cash answer to the exact synchronized registered listing.
    [HarmonyPatch(typeof(VehicleForSale_Display), nameof(VehicleForSale_Display.MouseHover))]
    internal static class VehicleSalePriceQuotePatch
    {
        private sealed class Quote
        {
            internal readonly float Price; internal readonly int Cash;
            internal Quote(float price, int cash) { Price = price; Cash = cash; }
        }
        [ThreadStatic] private static Quote? active;
        private static void Prefix(VehicleForSale_Display __instance, out Quote? __state)
        {
            __state = active; active = null;
            var world = MonMPPlugin.Instance?.World; var sales = SharedVehicleSales.Active;
            if (world?.Economy.BlocksLocalChanges != true ||
                sales?.TryGetQuotePrice(__instance, out float price) != true) return;
            active = new Quote(price, sales.QuoteCashCents);
        }
        private static Exception? Finalizer(Quote? __state, Exception? __exception)
        { active = __state; return __exception; }
        internal static bool TryResolve(float price, out bool canAfford)
        {
            var quote = active;
            if (quote == null || price != quote.Price) { canAfford = false; return false; }
            canAfford = quote.Cash >= UnityEngine.Mathf.RoundToInt(price * 100f); return true;
        }
    }

    // The native method tests the host-local player's hands. For a validated guest request it must
    // use SharedItems' checked buyer lease instead; this override lasts only during that call.
    [HarmonyPatch(typeof(PlayerInteractable), "get_IsHoldingAnObject")]
    internal static class VehicleSaleBuyerHandsPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref bool __result)
        {
            var sales = SharedVehicleSales.Active;
            if (sales?.IsRemoteExecution() != true) return true;
            __result = false;
            return false;
        }
    }
}
