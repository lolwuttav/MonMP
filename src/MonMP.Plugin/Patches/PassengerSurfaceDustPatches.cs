using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Effects;
using NWH.VehiclePhysics2.GroundDetection;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SurfaceParticleSystem), nameof(SurfaceParticleSystem.Update))]
    internal static class PassengerSurfaceDustPatch
    {
        private static void Prefix(SurfaceParticleSystem __instance, SurfacePreset ___surfacePreset)
            => PassengerSurfaceDust.BeforeUpdate(__instance, ___surfacePreset);
        private static void Postfix(SurfaceParticleSystem __instance, VehicleController ____vc, SurfacePreset ___surfacePreset)
            => PassengerSurfaceDust.AfterUpdate(__instance, ____vc, ___surfacePreset);
    }

    // Clear an already emitted world-space trail immediately upon sitting, even when the
    // vehicle has stopped and its wheel effect is not updating. No persistent override.
    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.EnterExitVehicle))]
    internal static class PassengerSurfaceDustVehicleEntryPatch
    {
        private static void Postfix(Gameplay __instance) => PassengerSurfaceDust.ClearOnSeatTransition(__instance);
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.EnterExitChair))]
    internal static class PassengerSurfaceDustChairEntryPatch
    {
        private static void Postfix(Gameplay __instance) => PassengerSurfaceDust.ClearOnSeatTransition(__instance);
    }
}
