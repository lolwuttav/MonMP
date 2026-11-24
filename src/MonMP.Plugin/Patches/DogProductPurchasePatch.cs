using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // The native override can reject a guest locally before it reaches Cash_Manager.TryToBuy.
    // Route the click through SharedStores first so the host's Leo state decides eligibility.
    [HarmonyPatch(typeof(ItemForSale_Display_DogProduct), "MouseDown")]
    internal static class DogProductPurchasePatch
    {

#if MONMP_DEVELOPMENT
        internal static int TestUnboundBlocks { get; private set; }


        internal static void ResetTestCounters() => TestUnboundBlocks = 0;
#endif


        private static bool Prefix(ItemForSale_Display_DogProduct __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.World?.Stores.Request(__instance) == true) return false;
            if (plugin?.Session?.IsRunning != true) return true;

#if MONMP_DEVELOPMENT
            if (plugin.TestOptions.Enabled) TestUnboundBlocks++;
#endif

            return false;
        }
    }
}
