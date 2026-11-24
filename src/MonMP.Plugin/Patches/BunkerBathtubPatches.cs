using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class BunkerBathtubButtonPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(Bunker_Bathtub_Button), nameof(Bunker_Bathtub_Button.MouseDown));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Bunker_Bathtub_Button __instance) =>
            MonMP.Plugin.MonMPPlugin.Instance?.World?.BunkerBathtub.AllowMutation(__instance) ?? true;
    }

    [HarmonyPatch(typeof(Bunker_Bathtub), nameof(Bunker_Bathtub.DetectorTriggered))]
    internal static class BunkerBathtubDetectorPatch
    {
        private static bool Prefix(Bunker_Bathtub __instance) =>
            MonMP.Plugin.MonMPPlugin.Instance?.World?.BunkerBathtub.AllowDetector(__instance) ?? true;
    }
}
