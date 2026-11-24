using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Police_SpotDetails), "FixedUpdated_WaitPlayerFarEnough")]
    internal static class PoliceWarningWaitPatch
    {
        private static bool Prefix(Police_SpotDetails __instance) =>
            MonMPPlugin.Instance?.World?.PoliceSpots.WaitingForRemote(__instance) != true;
    }
}
