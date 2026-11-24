using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // The native override rejects a guest click from its local ToDo snapshot before reaching
    // Cash_Manager.TryToBuy. Route it to the host, which owns the authoritative ToDo state.
    [HarmonyPatch(typeof(ItemForSale_Display_ToDoListToUnlock), "MouseDown")]
    internal static class ToDoListDisplayPurchasePatch
    {
        private static bool Prefix(ItemForSale_Display_ToDoListToUnlock __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (plugin.World?.Stores == null)
                plugin.ReportItemUseUnavailable("Wait for the shared world before buying from this display.");
            else
                plugin.World.Stores.RequestProgressionDisplay(__instance);
            return false;
        }
    }
}
