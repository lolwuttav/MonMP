using System;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // A guest's chainsaw fells and splits through the host; its own trees change only by replica.
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Cut))]
    internal static class ForestFellPatch
    {
        private static bool Prefix(TreeBase __instance, Vector3 PushOrientation, out SharedForest.NativeReplacementScope? __state)
        {
            var forest = MonMPPlugin.Instance?.World?.Forest;
            __state = forest?.BeginNativeFell(__instance);
            return forest?.Intercept(__instance, PushOrientation) != true;
        }
        private static void Postfix(SharedForest.NativeReplacementScope? __state) => __state?.Complete();
        private static Exception? Finalizer(Exception? __exception, SharedForest.NativeReplacementScope? __state)
        {
            __state?.Dispose(); return __exception;
        }
    }
    [HarmonyPatch(typeof(TreeTrunk), nameof(TreeTrunk.CutAppart))]
    internal static class ForestSplitPatch
    {
        private static bool Prefix(TreeTrunk __instance) => MonMPPlugin.Instance?.World?.Forest.Intercept(__instance) != true;
        private static void Postfix(TreeTrunk __instance, bool __runOriginal)
        {
            if (__runOriginal) MonMPPlugin.Instance?.World?.Forest.NativeSplit(__instance);
        }
    }
    // Guests never run the daily cuttings regrowth or sapling growth; the host's records drive both.
    [HarmonyPatch(typeof(TreeBase), "NewDayEvent")]
    internal static class ForestRegrowPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Forest.GuestBlocks != true;
        private static void Postfix(TreeBase __instance) => MonMPPlugin.Instance?.World?.Forest.RefreshPruningTarget(__instance);
    }
    [HarmonyPatch(typeof(Tool_PruningLoppers), nameof(Tool_PruningLoppers.Equiped))]
    internal static class PruningEquippedTargetsPatch
    {
        private static void Postfix(Tool_PruningLoppers __instance) => MonMPPlugin.Instance?.World?.Forest.PruningEquipped(__instance);
    }
    [HarmonyPatch(typeof(Tool_PruningLoppers), nameof(Tool_PruningLoppers.UnEquiped))]
    internal static class PruningUnequippedTargetsPatch
    {
        private static void Postfix(Tool_PruningLoppers __instance) => MonMPPlugin.Instance?.World?.Forest.PruningUnequipped(__instance);
    }
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Start))]
    internal static class PruningTreeStartedTargetsPatch
    {
        private static void Postfix(TreeBase __instance)
        {
            var forest = MonMPPlugin.Instance?.World?.Forest;
            forest?.NativeReady(__instance);
            forest?.RefreshPruningTarget(__instance);
        }
    }
    [HarmonyPatch(typeof(Tree_Growing), nameof(Tree_Growing.Start))]
    internal static class ForestSaplingReadyPatch
    {
        private static void Postfix(Tree_Growing __instance) => MonMPPlugin.Instance?.World?.Forest.NativeReady(__instance);
    }
    [HarmonyPatch(typeof(TreeTrunk), nameof(TreeTrunk.Start))]
    internal static class ForestTrunkReadyPatch
    {
        private static void Postfix(TreeTrunk __instance) => MonMPPlugin.Instance?.World?.Forest.NativeReady(__instance);
    }
    [HarmonyPatch(typeof(Garden_TreeCuttings), nameof(Garden_TreeCuttings.Start))]
    internal static class PruningCuttingStartedTargetsPatch
    {
        private static void Postfix(Garden_TreeCuttings __instance) => MonMPPlugin.Instance?.World?.Forest.RefreshPruningTarget(__instance);
    }
    [HarmonyPatch(typeof(Tree_Growing), "NewDay")]
    internal static class ForestGrowPatch
    {
        private static bool Prefix(Tree_Growing __instance, out SharedForest.NativeReplacementScope? __state)
        {
            var forest = MonMPPlugin.Instance?.World?.Forest;
            __state = forest?.BeginNativeGrowth(__instance);
            return forest?.GuestBlocks != true;
        }
        private static void Postfix(SharedForest.NativeReplacementScope? __state) => __state?.Complete();
        private static Exception? Finalizer(Exception? __exception, SharedForest.NativeReplacementScope? __state)
        {
            __state?.Dispose(); return __exception;
        }
    }
    [HarmonyPatch(typeof(TreeBase), "Awake")]
    internal static class ForestGrowingTreeAwakePatch
    {
        private static void Postfix(TreeBase __instance) => SharedForest.ObserveGrowingTree(__instance);
    }
    // Pruning/fertilizing predict locally; destructive sapling cuts and paid placements wait for
    // the host. Prune/harvest rewards are produced only by the accepted host source action.
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.GrabCuttings))]
    internal static class ForestPrunePatch
    {
        private static void Prefix(TreeBase __instance) { if (__instance.Get_HasCuttings) MonMPPlugin.Instance?.World?.Forest.Mirror(__instance, MonMP.Core.World.ForestOp.Prune); }
    }
    [HarmonyPatch(typeof(Tree_Growing), nameof(Tree_Growing.Fertilize))]
    internal static class ForestFertilizePatch
    {
        private static void Prefix(Tree_Growing __instance) { if (!__instance.Get_IsFertilized) MonMPPlugin.Instance?.World?.Forest.Mirror(__instance, MonMP.Core.World.ForestOp.Fertilize); }
    }
    [HarmonyPatch(typeof(Tree_Growing), nameof(Tree_Growing.Cut))]
    internal static class ForestSaplingCutPatch
    {
        private static bool Prefix(Tree_Growing __instance) => MonMPPlugin.Instance?.World?.Forest.InterceptSaplingCut(__instance) != true;
    }
    [HarmonyPatch(typeof(Garden_TreeCuttings), nameof(Garden_TreeCuttings.MouseDown))]
    internal static class ForestPlantPatch
    {
        private static bool Prefix(Garden_TreeCuttings __instance) => MonMPPlugin.Instance?.World?.Forest.PlantCutting(__instance) != true;
    }
    // Awards are produced by the host only after accepting the source prune/harvest. A guest's
    // native tool may still animate and predict its crop/basket, but never creates adoptable stock.
    [HarmonyPatch(typeof(Tool_PruningLoppers), "SpawnAddTreeCutting")]
    internal static class GuestCuttingSpawnPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Items.GuestRunning != true;
    }
    [HarmonyPatch(typeof(Tool_HarvestBasket), "SpawnAddSeeds")]
    internal static class GuestHarvestSeedSpawnPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Items.GuestRunning != true;
    }
}
