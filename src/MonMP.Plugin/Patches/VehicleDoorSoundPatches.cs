using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class VehicleDoorSoundRelayPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SOUND_Vehicle_Door), nameof(SOUND_Vehicle_Door.Open), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(SOUND_Vehicle_Door), nameof(SOUND_Vehicle_Door.Close), Type.EmptyTypes);
        }
        private static void Postfix(SOUND_Vehicle_Door __instance, MethodBase __originalMethod) =>
            VehicleDoorPresentation.Native(__instance, __originalMethod.Name == nameof(SOUND_Vehicle_Door.Open));
    }
}
