using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedBigPackOutputPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Item_WeedPreRollBigPack), nameof(Item_WeedPreRollBigPack.Use));
            yield return AccessTools.Method(typeof(Item_WeedPreRollHugePack), nameof(Item_WeedPreRollHugePack.Use));
        }
        private static void Prefix(InteractableItems __instance, out SharedItems.NativePackOutputScope? __state) =>
            __state = MonMPPlugin.Instance?.Items?.BeginNativePackOutputCapture(__instance);
        private static void Postfix(bool __result, SharedItems.NativePackOutputScope? __state) =>
            MonMPPlugin.Instance?.Items?.EndNativePackOutputCapture(__state, __result);
        private static Exception? Finalizer(Exception? __exception, SharedItems.NativePackOutputScope? __state)
        {
            MonMPPlugin.Instance?.Items?.EndNativePackOutputCapture(__state, used: false);
            return __exception;
        }
    }
    [HarmonyPatch(typeof(InteractableItems), nameof(InteractableItems.Awake))]
    internal static class SharedPackOutputAwakePatch
    {
        private static void Postfix(InteractableItems __instance)
        {
            var items = MonMPPlugin.Instance?.Items;
            items?.ObserveNativePackOutput(__instance);
            items?.ObserveNativeProductionOutput(__instance);
        }
    }
}
