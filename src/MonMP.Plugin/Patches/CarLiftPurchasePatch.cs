using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // The native override performs its Speedway progression check locally and places the lift
    // after TryToBuy. Route the exact subclass through SharedStores so the host validates both.
    [HarmonyPatch(typeof(ItemForSale_Display_CarLift), nameof(ItemForSale_Display_CarLift.MouseDown))]
    internal static class CarLiftPurchasePatch
    {
        private static bool Prefix(ItemForSale_Display_CarLift __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            __instance.MouseOut();
            if (plugin.World?.Stores?.Request(__instance) != true)
                plugin.ReportItemUseUnavailable("This car lift listing is unsupported or unavailable in the current multiplayer session.");
            return false;
        }
    }
}
