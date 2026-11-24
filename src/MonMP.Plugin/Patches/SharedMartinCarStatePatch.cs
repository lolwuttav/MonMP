using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedMartinCarStatePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(AI_MartinCar), "get_Get_Car_State")
                ?? throw new System.InvalidOperationException("Missing native Martin car state getter.");
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(AI_MartinCar __instance, ref AI_MartinCar.MartinCar_State __result)
        {
            var car = MonMPPlugin.Instance?.World?.MartinCar;
            if (car?.TryGetReplicatedCarState(__instance, out var state) != true) return true;
            __result = state;
            return false;
        }
    }
}
