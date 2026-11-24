using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    internal static class TireChangerAuthorityPatches
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Furniture_TireChanger_Handle), nameof(Furniture_TireChanger_Handle.MouseDown))!;
            yield return AccessTools.Method(typeof(Furniture_TireChanger_WheelDetection), nameof(Furniture_TireChanger_WheelDetection.MouseRightDown))!;
            yield return AccessTools.Method(typeof(Furniture_TireChanger_WheelDetection), "OnTriggerEnter")!;
        }

        internal static bool AllowNativeMutation() => TireChangerAuthorityPolicy.AllowNativeMutation(
            MonMPPlugin.Instance?.Session?.IsRunning == true,
            MonMPPlugin.Instance?.Session?.IsHost == true);
    }

    [HarmonyPatch(typeof(Furniture_TireChanger_Handle), nameof(Furniture_TireChanger_Handle.MouseDown))]
    internal static class TireChangerHandleAuthorityPatch
    {
        private static bool Prefix(Furniture_TireChanger_Handle __instance)
        {
            if (TireChangerAuthorityPatches.AllowNativeMutation()) return true;
            MonMPPlugin.Instance?.World?.TireChanger.InterceptHandle(__instance);
            return false;
        }

        private static void Postfix(Furniture_TireChanger_Handle __instance) =>
            MonMPPlugin.Instance?.World?.TireChanger.NativeCallbackCompleted(__instance);
    }

    [HarmonyPatch(typeof(Furniture_TireChanger_WheelDetection), nameof(Furniture_TireChanger_WheelDetection.MouseRightDown))]
    internal static class TireChangerWheelRemovalAuthorityPatch
    {
        private static bool Prefix(Furniture_TireChanger_WheelDetection __instance)
        {
            if (TireChangerAuthorityPatches.AllowNativeMutation()) return true;
            MonMPPlugin.Instance?.World?.TireChanger.InterceptRemoval(__instance);
            return false;
        }

        private static void Postfix(Furniture_TireChanger_WheelDetection __instance) =>
            MonMPPlugin.Instance?.World?.TireChanger.NativeCallbackCompleted(__instance);
    }

    // Trigger entry mutates the machine's private wheel slots and reparents the item.
    // Let the host's replicated item pose enter the host detector; guests only display it.
    [HarmonyPatch(typeof(Furniture_TireChanger_WheelDetection), "OnTriggerEnter")]
    internal static class TireChangerWheelDetectionAuthorityPatch
    {
        private static bool Prefix(Furniture_TireChanger_WheelDetection __instance, Collider other)
        {
            // A guest's held wheel or tire is only its hand pose here; the guest's own request mounts it
            // and releases the lease. Mounting it natively kept the guest holding a ghost while the rim
            // followed its hand, and the real request then failed as "already a wheel" (#122).
            if (TireChangerAuthorityPatches.AllowNativeMutation())
                return MonMPPlugin.Instance?.World?.TireChanger.HostMayMount(other) ?? true;
            MonMPPlugin.Instance?.World?.TireChanger.InterceptTrigger(__instance, other);
            return false;
        }

        private static void Postfix(Furniture_TireChanger_WheelDetection __instance) =>
            MonMPPlugin.Instance?.World?.TireChanger.NativeCallbackCompleted(__instance);
    }
}
