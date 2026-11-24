using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // A guest's equipped seed pack absorbing the pack it looks at: the absorbed pack is the host's.
    [HarmonyPatch(typeof(Tool_Seeds), nameof(Tool_Seeds.MouseDown))]
    internal static class GuestSeedPackMergePatch
    {
        private static void Prefix(Tool_Seeds __instance)
        {
            var items = MonMPPlugin.Instance?.Items;
            if (items?.GuestRunning != true || !__instance.isEquiped || __instance.Value1 <= 0) return;
            var target = Singleton<Gameplay>.i?.Player?.Get_CurrentInteractable?.GetComponent<Tool_Seeds>();
            if (target != null && target != __instance && target.Get_SeedsType == __instance.Get_SeedsType) items.GuestMergeSeeds(__instance, target);
        }
    }

    // A guest's held harvest basket pouring into the basket it looks at: both baskets are the host's.
    [HarmonyPatch(typeof(Tool_HarvestBasket), nameof(Tool_HarvestBasket.MouseDown))]
    internal static class GuestHarvestBasketPourPatch
    {
        private static void Prefix(Tool_HarvestBasket __instance, out (Tool_HarvestBasket? Target, float Before) __state)
        {
            __state = (null, __instance.Value1);
            if (MonMPPlugin.Instance?.Items?.GuestRunning != true || __instance.Value1 <= 0f) return;
            var target = Singleton<Gameplay>.i?.Player?.Get_CurrentInteractable?.GetComponent<Tool_HarvestBasket>();
            if (target != null && target != __instance) __state = (target, __instance.Value1);
        }
        private static void Postfix(Tool_HarvestBasket __instance, (Tool_HarvestBasket? Target, float Before) __state)
        {
            if (__state.Target != null && __instance.Value1 < __state.Before) MonMPPlugin.Instance?.Items?.GuestPourBasket(__instance, __state.Target);
        }
    }
}
