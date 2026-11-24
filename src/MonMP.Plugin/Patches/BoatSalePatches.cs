using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SmallCruiser_SellSign), nameof(SmallCruiser_SellSign.MouseDown))]
    internal static class SmallCruiserSalePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        { yield return AccessTools.Method(typeof(SmallCruiser_SellSign), nameof(SmallCruiser_SellSign.MouseDown))!; }

        internal static bool Prefix(SmallCruiser_SellSign __instance)
        {
            var adapter = SharedBoatSale.Active;
            if (adapter?.Purchase(BoatSaleKind.SmallCruiser, __instance) != true) return true;
            __instance.MouseOut();
            return false;
        }
    }

    [HarmonyPatch(typeof(BoatFishing_SellSign), nameof(BoatFishing_SellSign.MouseDown))]
    internal static class FishingBoatSalePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        { yield return AccessTools.Method(typeof(BoatFishing_SellSign), nameof(BoatFishing_SellSign.MouseDown))!; }

        internal static bool Prefix(BoatFishing_SellSign __instance)
        {
            var adapter = SharedBoatSale.Active;
            if (adapter?.Purchase(BoatSaleKind.FishingBoat, __instance) != true) return true;
            __instance.MouseOut();
            return false;
        }
    }

    // The native quote methods only use isCanAfford to select the text color. During a guest
    // session, resolve that one exact price against the replicated cash total while leaving fish
    // eligibility and native text generation intact.
    [HarmonyPatch]
    internal static class BoatSalePriceQuotePatch
    {
        internal sealed class Quote
        {
            internal readonly float Price;
            internal readonly int CashCents;
            internal Quote(float price, int cashCents) { Price = price; CashCents = cashCents; }
        }

        [ThreadStatic] private static Quote? active;

        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(SmallCruiser_SellSign), nameof(SmallCruiser_SellSign.Update_NameToAddAfter))!;
            yield return AccessTools.Method(typeof(BoatFishing_SellSign), nameof(BoatFishing_SellSign.Update_NameToAddAfter))!;
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Prefix(Interactable __instance, out Quote? __state)
        {
            __state = active;
            active = null;
            var world = MonMPPlugin.Instance?.World;
            var adapter = SharedBoatSale.Active;
            if (world?.Economy.BlocksLocalChanges != true || world.Channel.FullySynchronized != true ||
                adapter?.IsPriceQuoteFor(__instance) != true || !adapter.TryGetQuotePrice(__instance, out float price)) return;
            var cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            if (cash != null) active = new Quote(price, cash.GetCash);
        }

        private static Exception? Finalizer(Exception? __exception, Quote? __state)
        {
            active = __state;
            return __exception;
        }

        internal static bool TryResolve(float price, out bool canAfford)
        {
            var quote = active;
            if (quote == null || price != quote.Price ||
                (price != GameManager.PRICE_SmallCruiser && price != GameManager.PRICE_FishingBoat))
            { canAfford = false; return false; }
            canAfford = quote.CashCents >= price * 100f;
            return true;
        }
    }
}
