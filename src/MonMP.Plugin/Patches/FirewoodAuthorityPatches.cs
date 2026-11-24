using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    internal static class FirewoodAuthorityPatches
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(InteractableItems), nameof(InteractableItems.StartFire))!;
            yield return AccessTools.Method(typeof(BrotherHouse_ReceptionFirewood), "OnTriggerEnter")!;
            yield return AccessTools.Method(typeof(BrotherHouse), nameof(BrotherHouse.Receive), new[] { typeof(Item_FireWood) })!;
            yield return AccessTools.Method(typeof(BrotherHouse), nameof(BrotherHouse.ReceiveForGarage),
                new[] { typeof(Item_FireWood), typeof(bool) })!;
        }

        internal static bool AllowNativeMutation() => FirewoodAuthorityPolicy.AllowNativeMutation(
            MonMPPlugin.Instance?.Session?.IsRunning == true,
            MonMPPlugin.Instance?.Session?.IsHost == true);
    }

    // SharedItems already transports the loose log. Burning replaces its InteractableItems
    // component with a local WoodOnFire component, so only the host may start that transition.
    [HarmonyPatch(typeof(InteractableItems), nameof(InteractableItems.StartFire))]
    internal static class SharedFirewoodIgnitionAuthorityPatch
    {
        // Item_FireWood inherits this method, so discriminate here to avoid imposing the
        // firewood rule on the other fire-safe shared item types.
        private static bool Prefix(InteractableItems __instance)
        {
            if (SharedItems.ApplyingFire) return true;
            // A guest's presentation fire never spreads; the host's fire sets the shared items alight.
            if (SharedItems.GuestFireTicking) return false;
            // Native StartFire protects the local hand; protect remote item leases too (#158).
            if (MonMPPlugin.Instance?.Items?.HeldByRemotePlayer(__instance) == true) return false;
            return __instance is not Item_FireWood || FirewoodAuthorityPatches.AllowNativeMutation();
        }
        // The host publishes the burn so guests light their own copy instead of deleting it.
        private static void Postfix(InteractableItems __instance)
        {
            if (__instance != null && __instance.GetComponent<WoodOnFire>() != null) MonMPPlugin.Instance?.Items?.NoteBurned(__instance);
        }
    }

    // The detector calls one of the BrotherHouse receivers, which pays money, advances
    // progress/build counters, and destroys the log. Guests let the host's shared-item copy
    // reach this native sink after it is dropped into the receiver.
    [HarmonyPatch(typeof(BrotherHouse_ReceptionFirewood), "OnTriggerEnter")]
    internal static class SharedFirewoodReceptionTriggerAuthorityPatch
    {
        private static bool Prefix(UnityEngine.Collider other) =>
            other != null && other.CompareTag("Player") || FirewoodAuthorityPatches.AllowNativeMutation();
    }

    [HarmonyPatch(typeof(BrotherHouse), nameof(BrotherHouse.Receive), new[] { typeof(Item_FireWood) })]
    internal static class SharedFirewoodSaleAuthorityPatch
    {
        private static bool Prefix() => FirewoodAuthorityPatches.AllowNativeMutation();
    }

    [HarmonyPatch(typeof(BrotherHouse), nameof(BrotherHouse.ReceiveForGarage), new[] { typeof(Item_FireWood), typeof(bool) })]
    internal static class SharedFirewoodGarageAuthorityPatch
    {
        private static bool Prefix() => FirewoodAuthorityPatches.AllowNativeMutation();
    }
}
