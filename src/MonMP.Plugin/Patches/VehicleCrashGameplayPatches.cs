using System;
using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.Effects;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(DamageHandler), nameof(DamageHandler.HandleCollision))]
    internal static class VehicleCrashContactPatch
    {
        private static bool Prefix(DamageHandler __instance, out VehicleCrashGameplay.ContactScope __state, Collision collision)
        {
            __state = VehicleCrashGameplay.Begin(__instance);
            return VehicleCrashGameplay.AllowContact(__instance, collision);
        }
        private static void Postfix(Collision collision, VehicleCrashGameplay.ContactScope __state, bool __runOriginal) =>
            VehicleCrashGameplay.Contact(__state, collision, __runOriginal);
        private static Exception? Finalizer(Exception? __exception, VehicleCrashGameplay.ContactScope? __state)
        { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.VehicleCollision))]
    internal static class VehicleCrashOccupantPatch
    {
        private static bool Prefix(Gameplay __instance) => VehicleCrashGameplay.AllowNative(__instance);
        private static void Postfix(Gameplay __instance, Collision collision, bool __runOriginal) =>
            VehicleCrashGameplay.NativeApplied(__instance, collision, __runOriginal);
    }
}
