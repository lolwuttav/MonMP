using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedSchoolBusStatePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(AI_SchoolBus), "get_Get_Bus_State")
                ?? throw new InvalidOperationException("Missing native school-bus state getter.");
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(AI_SchoolBus __instance, ref AI_SchoolBus.SchoolBus_State __result)
        {
            var bus = MonMPPlugin.Instance?.World?.SchoolBus;
            if (bus?.TryGetReplicatedBusState(__instance, out var state) != true) return true;
            __result = state;
            return false;
        }
    }
}
