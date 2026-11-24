using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Item_Poutine), "Tick")]
    internal static class SharedFoodTickPatch
    {
        private static bool Prefix(Item_Poutine __instance) => MonMPPlugin.Instance?.Items?.AllowLocalFoodUpdate(__instance) != false;
    }
    [HarmonyPatch(typeof(Item_Poutine), "set_Get_IsRefrigerated")]
    internal static class SharedFoodRefrigerationPatch
    {
        private static bool Prefix(Item_Poutine __instance) => MonMPPlugin.Instance?.Items?.AllowLocalFoodUpdate(__instance) != false;
    }
    [HarmonyPatch(typeof(Item_Poutine), "Perished")]
    internal static class SharedFoodPerishedMaterialPatch
    {
        private static bool Prefix(Item_Poutine __instance)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true) return true;
            FoodState.Perished(__instance);
            return false;
        }
    }
}
