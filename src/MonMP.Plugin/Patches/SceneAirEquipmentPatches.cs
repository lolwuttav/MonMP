using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(InteractableTools), nameof(InteractableTools.canEquip))]
    internal static class SceneAirEquipmentCanEquipPatch
    {
        private static bool Prefix(InteractableTools __instance, ref bool __result)
        {
            var world = MonMPPlugin.Instance?.World;
            if (world?.Bolts?.AllowNativeImpactEquip(__instance) == false ||
                world?.SceneAirEquipment.AllowNativeEquip(__instance) == false)
            { __result = false; return false; }
            return true;
        }
    }
}
