using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Scrapyard_Container_SellMetal), nameof(Scrapyard_Container_SellMetal.Button_SellMetal))]
    internal static class MetalSaleButtonPatch
    {
        private static bool Prefix(Scrapyard_Container_SellMetal __instance) =>
            MonMPPlugin.Instance?.World?.MetalSales.Intercept(__instance) != true;
    }
}
