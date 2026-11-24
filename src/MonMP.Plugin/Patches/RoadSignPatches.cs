using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(BendOnCollision), nameof(BendOnCollision.VehicleCollision))]
    internal static class RoadSignCollisionPatch
    {
        private static bool Prefix(BendOnCollision __instance, Collision collision) =>
            MonMPPlugin.Instance?.World?.RoadSigns.BeforeCollision(__instance, collision) ?? true;
        private static void Postfix(BendOnCollision __instance, bool __runOriginal) =>
            MonMPPlugin.Instance?.World?.RoadSigns.AfterCollision(__instance, __runOriginal);
    }
}
