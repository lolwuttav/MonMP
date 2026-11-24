using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Guests submit a quoted purchase request; a host-local click still runs the game's sign callback.
    [HarmonyPatch(typeof(EntreposageGarage_PancarteForSale), nameof(EntreposageGarage_PancarteForSale.MouseDown))]
    internal static class StorageGarageSaleSignPatch
    {
        private static bool Prefix(EntreposageGarage_PancarteForSale __instance, out bool __state)
        {
            __state = false;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            var sale = plugin.World?.StorageGarageSale;
            if (sale == null)
            {
                plugin.ReportItemUseUnavailable("The garage purchase is unavailable until the multiplayer world is initialized.");
                __instance.MouseOut();
                return false;
            }
            bool consumed = sale.InterceptSignClick(__instance, out __state);
            if (consumed)
            {
                __instance.MouseOut();
                return false;
            }
            return true;
        }

        private static Exception? Finalizer(EntreposageGarage_PancarteForSale __instance, bool __state, Exception? __exception)
        {
            if (__state) MonMPPlugin.Instance?.World?.StorageGarageSale?.FinishLocalClick(__instance, __exception);
            return __exception;
        }
    }

    // Block any bypass of the registered native path while a multiplayer session is active.
    [HarmonyPatch(typeof(EntreposageGarage), nameof(EntreposageGarage.BuyThisGarage))]
    internal static class StorageGarageSaleNativePurchasePatch
    {
        private static bool Prefix(EntreposageGarage __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (plugin.World?.StorageGarageSale?.AllowNativePurchase(__instance) == true) return true;
            plugin.ReportItemUseUnavailable("The garage purchase must be approved by the multiplayer host.");
            return false;
        }
    }
}
