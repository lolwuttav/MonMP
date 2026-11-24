using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Store_PoliceInterceptor), nameof(Store_PoliceInterceptor.PlayerDetected),
        new[] { typeof(Police_SpotDetails), typeof(bool) })]
    internal static class PoliceRadarPatch
    {
        private static bool Prefix(Police_SpotDetails _spotDetails) =>
            MonMPPlugin.Instance?.World?.PoliceSpots.DetectedLocally(_spotDetails) != true;
    }
}
