using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Start is virtual and overridden by both native seat types. Observe each
    // completed initialization, including seats spawned between fallback scans.
    [HarmonyPatch(typeof(Furniture_Chair), nameof(Furniture_Chair.Start))]
    internal static class FurnitureChairDiscoveryPatch
    {
        private static void Postfix(Furniture_Chair __instance) => FurnitureSeatSources.NotifyStarted(__instance);
    }

    [HarmonyPatch(typeof(Furniture_Decoration_Seat), nameof(Furniture_Decoration_Seat.Start))]
    internal static class FurnitureDecorationSeatDiscoveryPatch
    {
        private static void Postfix(Furniture_Decoration_Seat __instance) => FurnitureSeatSources.NotifyStarted(__instance);
    }
}
