using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Vehicle_WindowTint), "Start")]
    internal static class SharedTintStartPatch
    {
        private static void Postfix(Vehicle_WindowTint __instance) =>
            MonMPPlugin.Instance?.World?.WindowTints?.OnNativeStart(__instance);
    }

    [HarmonyPatch(typeof(Tool_WindowTint), nameof(Tool_WindowTint.MouseDown))]
    internal static class SharedTintToolPatch
    {
        private static bool Prefix(Tool_WindowTint __instance) =>
            MonMPPlugin.Instance?.World?.WindowTints?.InterceptTool(__instance, false) ?? true;
    }

    [HarmonyPatch(typeof(Tool_WindowTintRemover), nameof(Tool_WindowTintRemover.MouseDown))]
    internal static class SharedTintRemoverPatch
    {
        private static bool Prefix(Tool_WindowTintRemover __instance) =>
            MonMPPlugin.Instance?.World?.WindowTints?.InterceptTool(__instance, true) ?? true;
    }

    [HarmonyPatch(typeof(Vehicle_WindowTint), nameof(Vehicle_WindowTint.ApplyTint))]
    internal static class SharedTintApplyPatch
    {
        private static bool Prefix(Vehicle_WindowTint __instance, ref bool __result)
        {
            if (MonMPPlugin.Instance?.World?.WindowTints?.AllowNativeMutation(__instance) != false) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Vehicle_WindowTint), nameof(Vehicle_WindowTint.RemoveTint))]
    internal static class SharedTintRemovePatch
    {
        private static bool Prefix(Vehicle_WindowTint __instance) =>
            MonMPPlugin.Instance?.World?.WindowTints?.AllowNativeMutation(__instance) ?? true;
    }

    [HarmonyPatch(typeof(Vehicle_WindowTint), nameof(Vehicle_WindowTint.LoadSaveSystem))]
    internal static class SharedTintLoadPatch
    {
        private static bool Prefix(Vehicle_WindowTint __instance) =>
            MonMPPlugin.Instance?.World?.WindowTints?.AllowNativeMutation(__instance) ?? true;
    }
}
