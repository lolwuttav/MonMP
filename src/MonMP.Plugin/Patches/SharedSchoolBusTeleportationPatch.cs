using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // #146: preserve the native route/checkpoint decision, but keep it from fast-forwarding
    // past a guest whose fresh pose is still near the bus or its selected destination.
    [HarmonyPatch]
    internal static class SharedSchoolBusTeleportationPatch
    {
        private static readonly FieldInfo CheckpointIndexField = AccessTools.Field(typeof(AI_SchoolBus), "_index_Checkpoint")
            ?? throw new InvalidOperationException("Missing native AI_SchoolBus._index_Checkpoint field.");

        internal static IEnumerable<MethodBase> Targets()
        {
            var method = AccessTools.Method(typeof(AI_SchoolBus), "CheckIfTeleportationNeeded", new[] { typeof(int) })
                ?? throw new InvalidOperationException("Missing native AI_SchoolBus.CheckIfTeleportationNeeded(int).");
            if (method.IsStatic || method.ReturnType != typeof(AI_SchoolBus_Checkpoint))
                throw new InvalidOperationException("Unsupported native AI_SchoolBus.CheckIfTeleportationNeeded(int) signature.");
            yield return method;
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Prefix(AI_SchoolBus __instance, out int __state)
            => __state = (int)CheckpointIndexField.GetValue(__instance);

        private static void Postfix(AI_SchoolBus __instance, int __state, ref AI_SchoolBus_Checkpoint __result)
        {
            if (__result == null) return;
            if (MonMPPlugin.Instance?.World?.SchoolBus?.VetoRemoteCheckpointSkip(__instance, __result, __state) == true)
                __result = null!;
        }
    }
}
