using System;
using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.WheelController3D;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(WheelController), "HitUpdate")]
    internal static class FrozenCargoWheelQueryPatch
    {
        private static void Prefix(WheelController __instance, out (FrozenCargoWheelQuery? Scope, int Mask) __state)
        {
            __state = (null, __instance.layerMask);
            if (__instance.parent == null) return;
            var scope = MonMPPlugin.Instance?.Items?.BeginCargoWheelQuery(__instance.parent.transform);
            if (scope == null) return;
            __state = (scope, __instance.layerMask);
            __instance.layerMask = __state.Mask & ~(1 << 2);
        }
        private static void Finalizer(WheelController __instance, (FrozenCargoWheelQuery? Scope, int Mask) __state)
        {
            if (__state.Scope == null) return;
            try { __instance.layerMask = __state.Mask; }
            finally { __state.Scope.Dispose(); }
        }
    }
}
