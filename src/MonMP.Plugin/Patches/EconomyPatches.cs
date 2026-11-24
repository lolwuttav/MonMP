using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SOUND_General), nameof(SOUND_General.Spent))]
    internal static class SharedCashSpentCuePatch
    {
        private static void Postfix() => MonMPPlugin.Instance?.World?.Economy.RelayCashCue(true);
    }
    [HarmonyPatch(typeof(SOUND_General), nameof(SOUND_General.Received))]
    internal static class SharedCashReceivedCuePatch
    {
        private static void Postfix() => MonMPPlugin.Instance?.World?.Economy.RelayCashCue(false);
    }

    [HarmonyPatch]
    internal static class SharedEconomyMutationPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { "HardMode_NewSave", "NewDay", "Event_Spent", "Event_Received", "Event_Received_NowAndEachSunday", "LoadSaveSystem", "TryToBuy_Vehicle" })
                yield return AccessTools.Method(typeof(Cash_Manager), name);
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(MethodBase __originalMethod)
        {
            var world = MonMPPlugin.Instance?.World;
            return world?.Economy.BlocksLocalChanges != true || world?.Stores.AllowNativeEconomyMutation(__originalMethod.Name) == true ||
                world?.VehicleSales?.AllowNativeEconomyMutation(__originalMethod.Name) == true ||
                world?.DealerBunkerPurchase.AllowNativeEconomyMutation(__originalMethod.Name) == true ||
                world?.PaintMixing.AllowNativeEconomyMutation(__originalMethod.Name) == true ||
                world?.BarDrink.AllowNativeEconomyMutation(__originalMethod.Name) == true;
        }
    }
    [HarmonyPatch(typeof(Cash_Manager), "TryToBuy")]
    internal static class SharedEconomyPurchasePatch
    {
        private static bool Prefix(ItemForSale_Display _itemForSale, ref InteractableItems __result)
        {
            var world = MonMPPlugin.Instance?.World;
            if (world?.Stores.Request(_itemForSale) == true) { __result = null!; return false; }
            if (world?.Stores.AllowNativePurchaseCall(_itemForSale) == true) return true;
            if (world?.Economy.BlocksLocalChanges != true) return true;
            __result = null!; return false;
        }
    }
    [HarmonyPatch(typeof(PlayerInteractable), "get_IsHoldingAnObject")]
    internal static class PurchaseBuyerHandsPatch
    {
        private static bool Prefix(ref bool __result)
        {
            // Only while executing a validated remote purchase on the host: native purchase eligibility
            // must use the buyer's checked hands, rather than the unrelated host player's held object.
            uint? peer = MonMPPlugin.Instance?.World?.Stores.ExecutingPeer;
            if (!peer.HasValue || peer.Value == 0) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(ItemForSale_Display), "MouseHover")]
    internal static class PurchasePriceHoverPatch
    {
        private static void Prefix(ItemForSale_Display __instance, out ItemForSale_Display? __state)
        {
            var stores = MonMPPlugin.Instance?.World?.Stores; __state = stores?.HoverDisplay;
            if (stores != null) stores.HoverDisplay = __instance;
        }
        private static void Finalizer(ItemForSale_Display? __state)
        { var stores = MonMPPlugin.Instance?.World?.Stores; if (stores != null) stores.HoverDisplay = __state; }
    }
    [HarmonyPatch(typeof(Cash_Manager), "isCanAfford")]
    internal static class SharedEconomyAffordPatch
    {
        private static bool Prefix(Cash_Manager __instance, float _price, ref bool __result)
        {
            if (DealerPriceQuotePatch.Quoting && MonMPPlugin.Instance?.World?.Economy.BlocksLocalChanges == true)
            { __result = __instance.GetCash >= UnityEngine.Mathf.RoundToInt(_price * 100f); return false; }
            if (MonMPPlugin.Instance?.World?.Stores.AllowNativeAffordabilityQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.VehicleSales?.AllowNativeAffordabilityQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.DealerBunkerPurchase.AllowNativeAffordabilityQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.PaintMixing.AllowNativeAffordabilityQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.BarDrink.AllowNativeAffordabilityQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.MeetRace?.AllowNativeAffordabilityQuote(_price) == true) return true;
            if (MonMPPlugin.Instance?.World?.Stores.CanQuote(_price) == true ||
                MonMPPlugin.Instance?.World?.SugarShack.CanQuote(_price) == true) return true;
            if (CottagePriceQuotePatch.TryResolve(_price, out bool canAfford))
            { __result = canAfford; return false; }
            if (BoatSalePriceQuotePatch.TryResolve(_price, out canAfford))
            { __result = canAfford; return false; }
            if (VehicleSalePriceQuotePatch.TryResolve(_price, out canAfford))
            { __result = canAfford; return false; }
            if (MonMPPlugin.Instance?.World?.Economy.BlocksLocalChanges != true) return true;
            // Prevent native purchase callers from granting local goods before a suppressed debit.
            // Validated network purchases will use the host's affordability check.
            __result = false; return false;
        }
    }
}
