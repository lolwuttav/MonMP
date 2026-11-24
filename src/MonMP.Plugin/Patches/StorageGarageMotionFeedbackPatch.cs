using System.Collections;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(EntreposageGarage_Door), "DoorMoving", new[] { typeof(bool) })]
    internal static class StorageGarageMotionFeedbackPatch
    {
        private static void Postfix(EntreposageGarage_Door __instance, ref IEnumerator __result)
        {
            if (__result != null && MonMPPlugin.Instance?.World?.StorageGarages is Replication.SharedStorageGarages garages)
                __result = garages.CaptureDoorMotion(__instance, __result);
        }
    }
}
