using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // #166: VFX_VehicleWeedSmoke only tops Martin's hotbox smoke up while vehicle.IsAwake. A guest's
    // Martin car is a kinematic replica that never wakes, so its smoke decayed to nothing. Run the
    // native top-up line while the host's car is out of its sleep state.
    [HarmonyPatch]
    internal static class SharedMartinCarSmokePatch
    {
        private static readonly AccessTools.FieldRef<VFX_VehicleWeedSmoke, Vehicle> VehicleRef = AccessTools.FieldRefAccess<VFX_VehicleWeedSmoke, Vehicle>("vehicle");
        private static readonly AccessTools.FieldRef<VFX_VehicleWeedSmoke, float> MinimumRef = AccessTools.FieldRefAccess<VFX_VehicleWeedSmoke, float>("MinimumSmokeAmount");
        private static readonly AccessTools.FieldRef<VFX_VehicleWeedSmoke, float> StrengthRef = AccessTools.FieldRefAccess<VFX_VehicleWeedSmoke, float>("SmokeStrengh");

        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(VFX_VehicleWeedSmoke), "Update")
                ?? throw new System.InvalidOperationException("Missing native VFX_VehicleWeedSmoke.Update.");
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Prefix(VFX_VehicleWeedSmoke __instance)
        {
            var car = MonMPPlugin.Instance?.World?.MartinCar;
            if (car == null || !car.GuestAttached) return;
            var vehicle = VehicleRef(__instance);
            float minimum = MinimumRef(__instance);
            if (minimum <= 0f || vehicle == null || vehicle.IsAwake || vehicle.isAWindowOrDoorIsOpen || !car.RemoteAwake(vehicle)) return;
            ref float strength = ref StrengthRef(__instance);
            if (strength < minimum) strength += Time.deltaTime * 0.1f;
        }
    }
}
