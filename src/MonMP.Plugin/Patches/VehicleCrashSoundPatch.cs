using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // DamageHandler reports collisions before native audio decides whether/when to play them.
    // Relay the successful native start, preserving rim exclusions and the busy-source queue.
    [HarmonyPatch(typeof(CrashComponent), nameof(CrashComponent.PlayCollisionSound))]
    internal static class VehicleCrashSoundPatch
    {
        private static void Prefix(CrashComponent __instance, out bool __state) =>
            __state = __instance.Source?.isPlaying == true;

        private static void Postfix(CrashComponent __instance, Collision ___collisionData, bool __state, bool __runOriginal) =>
            VehicleCrashPresentation.Native(__instance, ___collisionData, __state, __runOriginal);
    }
}
