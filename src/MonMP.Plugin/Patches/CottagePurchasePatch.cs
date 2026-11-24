using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
namespace MonMP.Plugin.Patches
{
    // Intercept the guest's local sign before its native purchase can spend local cash.
    [HarmonyPatch(typeof(Cottage_SellSign), nameof(Cottage_SellSign.MouseDown))]
    internal static class CottagePurchasePatch
    {
        internal static bool Prefix(Cottage_SellSign __instance)
        {
            var adapter = SharedCottagePurchase.Active;
            if (adapter?.Purchase(__instance) != true) return true;
            __instance.MouseOut();
            return false;
        }
    }

    // The native sign uses isCanAfford only to color its displayed quote. Give that one synchronous
    // query the host's replicated cash total; the local MouseDown remains blocked and host-validated.
    [HarmonyPatch(typeof(Cottage_SellSign), nameof(Cottage_SellSign.Update_NameToAddAfter))]
    internal static class CottagePriceQuotePatch
    {
        internal sealed class Quote
        {
            internal readonly float Price;
            internal readonly int CashCents;
            internal Quote(float price, int cashCents) { Price = price; CashCents = cashCents; }
        }

        [ThreadStatic] private static Quote? active;

        private static void Prefix(Cottage_SellSign __instance, out Quote? __state)
        {
            __state = active;
            active = null;
            var world = MonMPPlugin.Instance?.World;
            var adapter = SharedCottagePurchase.Active;
            if (world?.Economy.BlocksLocalChanges != true || world.Channel.FullySynchronized != true ||
                adapter?.IsPriceQuoteFor(__instance) != true) return;
            var cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            if (cash != null) active = new Quote(SharedCottagePurchase.PurchasePrice, cash.GetCash);
        }

        private static Exception? Finalizer(Exception? __exception, Quote? __state)
        {
            active = __state;
            return __exception;
        }

        internal static bool TryResolve(float price, out bool canAfford)
        {
            var quote = active;
            if (quote == null || price != quote.Price || price != SharedCottagePurchase.PurchasePrice)
            { canAfford = false; return false; }
            canAfford = quote.CashCents >= price * 100f;
            return true;
        }
    }
}
