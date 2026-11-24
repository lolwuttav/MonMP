using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(InteractableItems), nameof(InteractableItems.FreezeInContainer))]
    internal static class VehicleCargoFreezePatch
    {
        // Vanilla's parking-brake detector must not freeze a pending drop before
        // its quiet interval, or destroy the body retained by an optional pin.
        private static bool Prefix(InteractableItems __instance, Transform _container) =>
            MonMPPlugin.Instance?.Items?.AllowNativeCargoFreeze(__instance, _container) ?? true;
    }
}
