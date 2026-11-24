using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(TrashRecycle), "ShowMessage")]
    internal static class SharedTrashRecycleFeedbackPatch
    {
        private static bool Prefix(TrashRecycle __instance, InteractableItems item) =>
            MonMPPlugin.Instance?.World?.TrashRecycle.Feedback(__instance, item) != true;
    }

    [HarmonyPatch(typeof(TrashRecycle), "EmptyTrash")]
    internal static class SharedTrashRecycleSweepPatch
    {
        private static bool Prefix(TrashRecycle __instance) =>
            MonMPPlugin.Instance?.World?.TrashRecycle.Sweep(__instance) != true;
    }
}
