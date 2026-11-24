using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class PoliceSpotPatches
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { "Enable_Spot", "Disable_Spot", "FixedUpdate", "EnableFlashingLights" })
                yield return AccessTools.Method(typeof(Police_SpotDetails), name);
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix() => MonMPPlugin.Instance?.World?.PoliceSpots.Guest != true;
        private static void Postfix(Police_SpotDetails __instance, MethodBase __originalMethod)
        {
            if (__originalMethod.Name == "Enable_Spot" || __originalMethod.Name == "Disable_Spot")
                MonMPPlugin.Instance?.World?.PoliceSpots.ResetWarning(__instance);
        }
    }
}
