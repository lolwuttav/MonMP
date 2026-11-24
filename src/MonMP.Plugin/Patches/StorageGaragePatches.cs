using HarmonyLib;
using NWH;

namespace MonMP.Plugin.Patches
{
    // Guests route storage garage door buttons to the host and leave loading/unloading the parked
    // vehicle to the host's record; the host counts remote players standing in a closing bay.
    [HarmonyPatch(typeof(EntreposageGarage), nameof(EntreposageGarage.ToggleDoor))]
    internal static class StorageGarageTogglePatch
    {
        private static bool Prefix(EntreposageGarage __instance, int _doorID) =>
            MonMPPlugin.Instance?.World?.StorageGarages.AllowToggle(__instance, _doorID) ?? true;
    }
    [HarmonyPatch(typeof(EntreposageGarage), nameof(EntreposageGarage.UnloadThisCar))]
    internal static class StorageGarageUnloadPatch
    {
        private static bool Prefix(EntreposageGarage __instance) =>
            MonMPPlugin.Instance?.World?.StorageGarages.AllowLoadUnload(__instance) ?? true;
    }
    [HarmonyPatch(typeof(EntreposageGarage), nameof(EntreposageGarage.LoadThisCar))]
    internal static class StorageGarageLoadPatch
    {
        private static bool Prefix(EntreposageGarage __instance) =>
            MonMPPlugin.Instance?.World?.StorageGarages.AllowLoadUnload(__instance) ?? true;
    }
    [HarmonyPatch(typeof(EntreposageGarage_Door), "Door_IsOKToClose")]
    internal static class StorageGarageCloseCheckPatch
    {
        private static bool Prefix(EntreposageGarage_Door __instance, ref bool __result)
        {
            if (MonMPPlugin.Instance?.World?.StorageGarages.SkipCloseCheck(__instance) != true) return true;
            __result = true;
            return false;
        }
    }
    [HarmonyPatch(typeof(EntreposageGarage_Door), nameof(EntreposageGarage_Door.isPlayerInBay), MethodType.Getter)]
    internal static class StorageGaragePlayerInBayPatch
    {
        private static void Postfix(EntreposageGarage_Door __instance, ref bool __result)
        {
            var replicated = MonMPPlugin.Instance?.World?.StorageGarages.PlayerInBay(__instance);
            if (replicated.HasValue) __result = replicated.Value;
        }
    }
}
