using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SaveProductionIteratorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NativeSaveProduction.Factories();
        private static void Postfix(MonoBehaviour __instance, MethodBase __originalMethod, IEnumerator __result) =>
            NativeSaveProduction.Track(__instance, __originalMethod.Name, __result);
    }

    [HarmonyPatch]
    internal static class SaveProductionStepPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NativeSaveProduction.Steps();
        private static void Prefix(IEnumerator __instance) => NativeSaveProduction.TrackStep(__instance);
    }
}
