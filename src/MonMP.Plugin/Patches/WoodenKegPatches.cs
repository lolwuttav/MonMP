using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Route only the keg's guest hand-use through its host authority adapter before SharedItems
    // applies the generic guest-use policy. Host and offline interaction keep the native path.
    [HarmonyPatch(typeof(PlayerInteractable), "UseKeyDown")]
    internal static class SharedWoodenKegUsePatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(PlayerInteractable __instance, InteractableItems ___HoldingItem,
            InteractableItems ___currentInteractableItem, bool __runOriginal) =>
            __runOriginal && ((___HoldingItem == null && __instance.Get_ActiveTool != null) ||
            !(SharedWoodenKegs.Current?.InterceptUseKeyDown(__instance,
                ___HoldingItem != null ? ___HoldingItem : ___currentInteractableItem) ?? false));
    }

    // A script-side direct Use call must not bypass the routed guest command.
    [HarmonyPatch(typeof(Item_WoodenKeg), nameof(Item_WoodenKeg.Use))]
    internal static class SharedWoodenKegDirectUsePatch
    {
        private static bool Prefix(Item_WoodenKeg __instance, ref bool __result)
        {
            if (SharedWoodenKegs.Current?.SuppressGuestDirectUse(__instance) != true) return true;
            __result = false;
            return false;
        }
    }

    // Every process receives UniStorm's minute event. Guest replicas consume host-published
    // Value1/Value2 only; native fermentation advances on the host and in offline play.
    [HarmonyPatch(typeof(Item_WoodenKeg), "Tick", new[] { typeof(int) })]
    internal static class SharedWoodenKegFermentationPatch
    {
        private static bool Prefix(Item_WoodenKeg __instance) =>
            SharedWoodenKegs.Current?.SuppressGuestFermentation(__instance) != true;
    }

    // Native Use creates and immediately grabs a local bottle. During a host-approved remote
    // extraction replace that transient object with a normal SharedItems lease for the requester.
    [HarmonyPatch(typeof(PlayerInteractable), nameof(PlayerInteractable.GrabNewItemBought))]
    internal static class SharedWoodenKegWineDeliveryPatch
    {
        private static bool Prefix(PlayerInteractable __instance, UnityEngine.GameObject _newItem) =>
            SharedWoodenKegs.Current?.RedirectNativeWinePack(__instance, _newItem) != true;
    }

    internal static class SharedWoodenKegPatchTargets
    {
        internal static IEnumerable<MethodBase> RequiredTargets()
        {
            yield return AccessTools.Method(typeof(PlayerInteractable), "UseKeyDown")
                ?? throw new MissingMethodException("PlayerInteractable.UseKeyDown");
            yield return AccessTools.Method(typeof(Item_WoodenKeg), nameof(Item_WoodenKeg.Use))
                ?? throw new MissingMethodException("Item_WoodenKeg.Use");
            yield return AccessTools.Method(typeof(Item_WoodenKeg), "Tick", new[] { typeof(int) })
                ?? throw new MissingMethodException("Item_WoodenKeg.Tick(int)");
            yield return AccessTools.Method(typeof(PlayerInteractable), nameof(PlayerInteractable.GrabNewItemBought))
                ?? throw new MissingMethodException("PlayerInteractable.GrabNewItemBought");
        }
    }
}
