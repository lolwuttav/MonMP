using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(RacecarBuild), "OnTriggerEnter")]
    internal static class RacecarBuildTriggerPatch
    {
        private static bool Prefix(RacecarBuild __instance, Collider other) =>
            SharedRacecarBuild.Current?.AllowNativeTrigger(__instance, other) != false;

        private static void Postfix(RacecarBuild __instance, Collider other) =>
            SharedRacecarBuild.Current?.NativeTriggerCompleted(__instance, other);
    }

    [HarmonyPatch(typeof(Speedway_RacecarToDo), "MouseDown")]
    internal static class SpeedwayRacecarRewardPatch
    {
        private static bool Prefix() => SharedRacecarBuild.Current?.AllowNativeRewardClaim != false;
    }
}
