using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Every garage press (wall button, scrap-free speedway button, vehicle remote opener) ends in the
    // garage's ButtonPressed; a guest's press becomes a host command instead of a local door move.
    [HarmonyPatch]
    internal static class GaragePressPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.Method(typeof(HomeGarage), nameof(HomeGarage.ButtonPressed));
            yield return AccessTools.Method(typeof(Speedway_Garage), nameof(Speedway_Garage.ButtonPressed));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(MonoBehaviour __instance) => MonMPPlugin.Instance?.World?.Garages.Intercept(__instance) != true;
    }
    // Safety detectors and generator cut-offs run on the host's garage only.
    [HarmonyPatch]
    internal static class GarageHostOnlyPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.Method(typeof(HomeGarage), nameof(HomeGarage.DetectorTriggered));
            yield return AccessTools.Method(typeof(HomeGarage), nameof(HomeGarage.GeneratorStopped));
            yield return AccessTools.Method(typeof(Speedway_Garage), nameof(Speedway_Garage.DetectorTriggered));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Garages.GuestBlocks != true;
    }
}
