using HarmonyLib;
using MonMP.Plugin.Diagnostics;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(BuggyBuild), "OnTriggerEnter")]
    internal static class BuggyBuildTriggerPatch
    {
        private static bool Prefix(BuggyBuild __instance, Collider other) =>
#if MONMP_DEVELOPMENT
            !PairedBuggyBuildTest.SuppressSavedTrigger(other) &&
#endif
            SharedBuggyBuild.Current?.AllowNativeTrigger(__instance, other) != false;

        private static void Postfix(BuggyBuild __instance, Collider other)
        {
#if MONMP_DEVELOPMENT
            if (!PairedBuggyBuildTest.SuppressSavedTrigger(other))
#endif
                SharedBuggyBuild.Current?.NativeTriggerCompleted(__instance, other);
        }
    }
}
