using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Tool_DryFoodPack), nameof(Tool_DryFoodPack.MouseDown))]
    internal static class SharedLeoDryFoodPatch
    {
        private static bool Prefix(Tool_DryFoodPack __instance) =>
            MonMPPlugin.Instance?.World?.LeoSupplies.InterceptDryFood(__instance) != true;
    }

    [HarmonyPatch(typeof(Tool_FertilizerPack), nameof(Tool_FertilizerPack.MouseDown))]
    internal static class SharedLeoBunkerFertilizerToolPatch
    {
        private static bool Prefix(Tool_FertilizerPack __instance) =>
            MonMPPlugin.Instance?.World?.LeoSupplies.InterceptBunkerFertilizer(__instance) != true;
    }

    [HarmonyPatch(typeof(Home_Bunker), nameof(Home_Bunker.AddFertilizer))]
    internal static class SharedLeoBunkerFertilizerGuardPatch
    {
        private static bool Prefix(Home_Bunker __instance, Tool_FertilizerPack tool_FertilizerPack, ref float __result)
        {
            var supplies = MonMPPlugin.Instance?.World?.LeoSupplies;
            if (supplies?.GuestBlocksBunker(__instance) != true) return true;
            __result = 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(Home_Bunker), "WaterPlants")]
    internal static class SharedLeoBunkerWaterPlantsPatch
    {
        private static bool Prefix(Home_Bunker __instance) =>
            MonMPPlugin.Instance?.World?.LeoSupplies.GuestBlocksBunker(__instance) != true;
    }

    [HarmonyPatch(typeof(Home_Bunker), nameof(Home_Bunker.Manually_WaterPlants))]
    internal static class SharedLeoManualBunkerWaterPlantsPatch
    {
        // A guest's button press asks the host to run the watering pass.
        private static bool Prefix(Home_Bunker __instance) =>
            MonMPPlugin.Instance?.World?.BunkerWatering.InterceptManualWater(__instance) != true &&
            MonMPPlugin.Instance?.World?.LeoSupplies.GuestBlocksBunker(__instance) != true;
    }

    [HarmonyPatch]
    internal static class SharedLeoWateringCanPatch
    {
        private static MethodInfo ResolveMoveNext()
        {
            var coroutine = AccessTools.Method(typeof(Tool_WateringCan), "CheckToWater");
            if (coroutine?.ReturnType != typeof(System.Collections.IEnumerator))
                throw new InvalidOperationException("Unsupported watering can coroutine.");
            return AccessTools.EnumeratorMoveNext(coroutine) ??
                throw new InvalidOperationException("Unsupported watering can iterator method.");
        }
        private static readonly MethodInfo MoveNext = ResolveMoveNext();
        private static FieldInfo ToolField => AccessTools.Field(MoveNext.DeclaringType, "<>4__this")
            ?? throw new InvalidOperationException("Unsupported watering can iterator state.");
        internal static IEnumerable<MethodInfo> Targets()
        {
            var target = MoveNext;
            if (target.ReturnType != typeof(bool) || ToolField.FieldType != typeof(Tool_WateringCan))
                throw new InvalidOperationException("Unsupported watering can iterator signature.");
            yield return target;
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static Tool_WateringCan? Tool(object iterator) => ToolField.GetValue(iterator) as Tool_WateringCan;
        private static void Prefix(object __instance, out float __state) => __state = Tool(__instance)?.Value1 ?? 0;
        private static void Postfix(object __instance, bool __result, float __state)
        {
            var tool = Tool(__instance);
            if (tool != null) MonMPPlugin.Instance?.World?.LeoSupplies.ObserveWateringCanTick(tool, __state, __result);
        }
    }

    [HarmonyPatch(typeof(Tool_WateringCan), nameof(Tool_WateringCan.MouseUp))]
    internal static class SharedLeoWateringCanReleasePatch
    {
        private static void Prefix(Tool_WateringCan __instance) =>
            MonMPPlugin.Instance?.World?.LeoSupplies.FlushWatering(__instance);
    }
}
