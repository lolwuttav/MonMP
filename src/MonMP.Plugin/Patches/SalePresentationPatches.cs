using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Host: a native sale receiver that accepted the item has parented it under the store for its
    // slide. Mark the shared item Sold so guests play the same slide instead of deleting their copy.
    [HarmonyPatch]
    internal static class SalePresentationPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Store_Sawmill), nameof(Store_Sawmill.Receive), new[] { typeof(Item_WoodLog) })!;
            yield return AccessTools.Method(typeof(Store_FarmKiosque), nameof(Store_FarmKiosque.Receive), new[] { typeof(Item_Fish) })!;
            yield return AccessTools.Method(typeof(Store_FederationSyrup), nameof(Store_FederationSyrup.Receive), new[] { typeof(Item_Barrel120) })!;
            yield return AccessTools.Method(typeof(Store_IslandHouses), nameof(Store_IslandHouses.Receive), new[] { typeof(Item_Barrel120) })!;
            yield return AccessTools.Method(typeof(BrotherHouse), nameof(BrotherHouse.Receive), new[] { typeof(Item_FireWood) })!;
            yield return AccessTools.Method(typeof(BrotherHouse), nameof(BrotherHouse.ReceiveForGarage), new[] { typeof(Item_FireWood), typeof(bool) })!;
            // #188/#190: fries, weed packs, maple wine and syrup crates slid on the host only.
            yield return AccessTools.Method(typeof(Store_PizzaSnack), nameof(Store_PizzaSnack.Receive), new[] { typeof(Item_FrenchFries) })!;
            yield return AccessTools.Method(typeof(Store_Dealer), nameof(Store_Dealer.ReceiveBigPack), new[] { typeof(Item_WeedPreRollBigPack) })!;
            yield return AccessTools.Method(typeof(Store_Dealer), nameof(Store_Dealer.ReceiveHugePack), new[] { typeof(Item_WeedPreRollHugePack), typeof(bool) })!;
            yield return AccessTools.Method(typeof(Store_Bar), nameof(Store_Bar.Receive), new[] { typeof(Item_MapleWineBottle) })!;
            yield return AccessTools.Method(typeof(Store_Bar), nameof(Store_Bar.Receive), new[] { typeof(Pack_MapleSyrupCrate) })!;
        }

        // A refused sale (full palette) leaves the item where it was; an accepted one re-parents it for the slide.
        private static void Prefix(object[] __args, out Transform? __state) =>
            __state = __args.Length > 0 && __args[0] is InteractableItems item && item != null ? item.transform.parent : null;

        private static void Postfix(object[] __args, Transform? __state)
        {
            if (MonMPPlugin.Instance?.Session?.IsHost == true && __args.Length > 0 && __args[0] is InteractableItems item && item != null &&
                item.transform.parent != null && item.transform.parent != __state)
                MonMPPlugin.Instance.Items?.NoteSold(item);
        }
    }

    // Host: a fish bucket sold at the kiosk slides every fish to the freezer. Guests saw them vanish.
    [HarmonyPatch(typeof(Store_FarmKiosque), nameof(Store_FarmKiosque.Receive), new[] { typeof(Tool_FishBucket) })]
    internal static class FishBucketSalePresentationPatch
    {
        private static readonly FieldInfo? Contents = AccessTools.Field(typeof(Tool_FishBucket), "FishesInBucket");

        private static void Prefix(Tool_FishBucket __0, out List<(Item_Fish Fish, Transform Parent)>? __state)
        {
            __state = null;
            if (MonMPPlugin.Instance?.Session?.IsHost != true || __0 == null || !(Contents?.GetValue(__0) is List<Item_Fish> fish)) return;
            __state = new List<(Item_Fish, Transform)>();
            foreach (var f in fish) if (f != null) __state.Add((f, f.transform.parent));
        }

        private static void Postfix(Tool_FishBucket __0, List<(Item_Fish Fish, Transform Parent)>? __state)
        {
            if (__state == null) return;
            foreach (var (fish, parent) in __state)
                if (fish != null && fish.transform.parent != null && fish.transform.parent != parent) MonMPPlugin.Instance?.Items?.NoteSold(fish);
            MonMPPlugin.Instance?.World?.FishBuckets?.CompleteHostSale(__0);
        }
    }
}
