using HarmonyLib;
using NWH;
using NWH.VehiclePhysics;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // The game voices engines through RealisticEngineSound (layered idle/low/med/high clips mixed at a
    // master volume near 0.1), fed by NWH2_RES from the NWH engine; NWH2_RES switches NWH's own
    // EngineRunning loop off. On a remote replica the NWH engine idles whatever the driver does, so the
    // layered sound never started and MonMP fell back to NWH's untuned loop at full level: other players'
    // engines peaked at 100% volume (#121). Feed the vanilla sound the driver's replicated engine instead.
    [HarmonyPatch(typeof(NWH2_RES), "Update")]
    internal static class RemoteVehicleRealisticEngineSoundPatch
    {
        private static readonly AccessTools.FieldRef<NWH2_RES, RealisticEngineSound> Res = AccessTools.FieldRefAccess<NWH2_RES, RealisticEngineSound>("res");
        private static readonly AccessTools.FieldRef<NWH2_RES, VehicleController> Vc = AccessTools.FieldRefAccess<NWH2_RES, VehicleController>("vc");
        private static readonly AccessTools.FieldRef<NWH2_RES, Camera> Cam = AccessTools.FieldRefAccess<NWH2_RES, Camera>("cam");
        private const float RadPerSecToRpm = 9.549297f;

        internal static bool Drives(VehicleController? vehicle) =>
            vehicle != null && vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote &&
            MonMPPlugin.Instance?.Session?.IsRunning == true && MonMPPlugin.Instance.Vehicles?.IsBound(vehicle) == true;

        internal static float ReplicatedRpm(VehicleController vehicle) =>
            MonMPPlugin.Instance?.Vehicles?.TryGetDriverState(vehicle, out var state) == true && state != null
                ? Mathf.Abs(state.EngineAngularVelocity) * RadPerSecToRpm : 0f;

        // A puppet another player's machine drives: the school bus, or a speedway event's AI car.
        private static bool PresentedEngine(VehicleController vehicle, out float rpm, out float speed, out bool gas)
        {
            rpm = 0f; speed = 0f; gas = false;
            var world = MonMPPlugin.Instance?.World;
            if (world == null) return false;
            if (world.SchoolBus != null && world.SchoolBus.RemoteEngine(vehicle, out rpm, out speed, out gas)) return true;
            return world.SpeedwayEvents != null && world.SpeedwayEvents.RemoteEngine(vehicle, out rpm, out speed, out gas);
        }

        private static bool Prefix(NWH2_RES __instance)
        {
            var vehicle = Vc(__instance); var res = Res(__instance);
            if (res != null && vehicle != null && PresentedEngine(vehicle, out float busRpm, out float busSpeed, out bool busGas))
            {
                if (busRpm <= 0f)
                {
                    if (res.enabled && res.engineCurrentRPM > 0f) { res.engineCurrentRPM -= 2000f * Time.deltaTime; res.gasPedalPressing = false; return false; }
                    res.enabled = false;
                    return false;
                }
                res.enabled = true;
                if (res.mainCamera != Cam(__instance)) res.mainCamera = Cam(__instance);
                res.engineCurrentRPM = busRpm; res.carCurrentSpeed = busSpeed; res.gasPedalPressing = busGas; res.isReversing = false;
                return false;
            }
            if (res == null || !Drives(vehicle) || MonMPPlugin.Instance!.Vehicles!.TryGetDriverState(vehicle!, out var state) != true || state == null) return true;
            // As NWH2_RES.Update, with the driver's replicated engine in place of the idling replica's.
            if (!vehicle!.powertrain.engine.IsRunning)
            {
                if (res.enabled && res.engineCurrentRPM > 0f) { res.engineCurrentRPM -= 2000f * Time.deltaTime; res.gasPedalPressing = false; return false; }
                res.enabled = false;
                return false;
            }
            res.enabled = true;
            if (res.mainCamera != Cam(__instance)) res.mainCamera = Cam(__instance);
            res.engineCurrentRPM = Mathf.Abs(state.EngineAngularVelocity) * RadPerSecToRpm;
            res.carCurrentSpeed = vehicle.Speed;
            res.gasPedalPressing = state.Throttle > 0.05f;
            res.isReversing = state.Gear < 0;
            return false;
        }
    }
}
