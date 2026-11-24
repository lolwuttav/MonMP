using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Sale receptions without their own host route: the pizza snack's fries pallet, the dealer's weed
    // packs (which also assembles 27 big packs into a huge one) and the bar's maple wine bottles. A guest's replica ran the native sale on
    // its private wallet, to-do list and pallet, and destroyed only its own copy. The host's replica of the
    // same item enters the host's reception too (following a guest's hand, or dropped in), so guests leave
    // the sale to it. The native receiver's accepted-item release below ends a guest's lease.
    [HarmonyPatch]
    internal static class SaleReceptionAuthorityPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Store_PizzaSnack_FriesReception), "OnTriggerEnter", new[] { typeof(Collider) });
            yield return AccessTools.Method(typeof(Store_Dealer_WeedReception), "OnTriggerEnter", new[] { typeof(Collider) });
            yield return AccessTools.Method(typeof(Store_Bar_MapleWineReception), "OnTriggerEnter", new[] { typeof(Collider) });
            // Barrels: the Receive patches already keep guests out; the host still takes a barrel out of a
            // guest's hand before selling it.
            yield return AccessTools.Method(typeof(Store_FederationSyrup_Reception), "OnTriggerEnter", new[] { typeof(Collider) });
            yield return AccessTools.Method(typeof(Store_IslandHouses_Reception), "OnTriggerEnter", new[] { typeof(Collider) });
            // #235: loose fish and fish buckets at the farm kiosk.
            yield return AccessTools.Method(typeof(Store_FarmKiosque_ReceptionFish), "OnTriggerEnter", new[] { typeof(Collider) });
        }

        private static bool Prefix(object __instance, Collider other, out System.IDisposable? __state)
        {
            __state = null;
            if (other == null || other.tag == "Player") return true;
            var plugin = MonMPPlugin.Instance;
            var items = plugin?.Items;
            var item = other.GetComponent<InteractableItems>();
            if (plugin?.Session?.IsRunning != true || items == null || item == null || !items.Handles(item)) return true;
            // A wooden keg only shows the native "extract the wine first" message.
            bool feedback = __instance is Store_Dealer_WeedReception reception &&
                plugin.World?.TriggerFeedback.Dealer(reception, item) == true;
            if (!plugin.Session.IsHost) return item is Item_WoodenKeg;
            if (feedback) __state = RemoteActionScope.Enter();
#if MONMP_DEVELOPMENT
            bool remote = items.HeldByRemotePlayer(item);
            if (plugin.TestOptions.Enabled) Debug.Log("MonMP sale reception entered: " + item.GetType().Name + (remote ? " (held by a guest)" : ""));
#endif

            return true;
        }

        private static void Postfix(System.IDisposable? __state) => __state?.Dispose();
        private static System.Exception? Finalizer(System.Exception? __exception, System.IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }

    // A native receiver calls CheckIfHoldingItemToCancel only after its type/capacity/
    // contents checks accept the sale, before moving or removing the item. Use that exact
    // boundary instead of duplicating all native eligibility rules in the trigger prefix.
    [HarmonyPatch]
    internal static class SaleReceiveReleaseScopePatch
    {
        [System.ThreadStatic] internal static InteractableItems? Receiving;
        private static IEnumerable<MethodBase> TargetMethods() => SalePresentationPatch.TargetMethods();

        private static void Prefix(object[] __args, out InteractableItems? __state)
        {
            __state = Receiving;
            Receiving = __args.Length > 0 ? __args[0] as InteractableItems : null;
        }

        private static void Finalizer(InteractableItems? __state) => Receiving = __state;
    }

    [HarmonyPatch(typeof(PlayerInteractable), nameof(PlayerInteractable.CheckIfHoldingItemToCancel))]
    internal static class SaleAcceptedItemReleasePatch
    {
        private static void Prefix(InteractableItems __0)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || !plugin.Session.IsHost || __0 == null ||
                !ReferenceEquals(SaleReceiveReleaseScopePatch.Receiving, __0)) return;
            var items = plugin.Items;
            if (items?.HeldByRemotePlayer(__0) == true) items.HostReclaim(__0);
        }
    }
}
