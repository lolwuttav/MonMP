using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Both native callbacks consume shared items and spawn new packs after a coroutine. A guest's
    // click runs on the host (Replication.SharedRemoteClicks), whose shared items carry the result.
    internal static class CannabisProductionGuard
    {
        internal static bool Allow(UnityEngine.MonoBehaviour machine)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            if (Replication.SharedRemoteClicks.Active?.Intercept(machine) == true) return false;
            plugin.ReportItemUseUnavailable("This cannabis machine is not registered in the shared world.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_CannabisGrinder), nameof(Furniture_CannabisGrinder.MouseDown))]
    internal static class CannabisGrinderInputPatch
    {
        private static bool Prefix(Furniture_CannabisGrinder __instance) => CannabisProductionGuard.Allow(__instance);
    }

    [HarmonyPatch(typeof(Furniture_CannabisPackager), nameof(Furniture_CannabisPackager.MouseDown))]
    internal static class CannabisPackagerInputPatch
    {
        private static bool Prefix(Furniture_CannabisPackager __instance) => CannabisProductionGuard.Allow(__instance);
    }
}
