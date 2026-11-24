using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH;
using NWH.DWP2.ShipController;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Engine), "Update")]
    internal static class BoatGuestEnginePresentationPatch
    {
        private static void Prefix(Engine __instance, out Part_FuelTank? __state) =>
            __state = MonMPPlugin.Instance?.World?.BoatMotion?.BeginNativeEngineUpdate(__instance);

        private static Exception? Finalizer(Part_FuelTank? __state, Exception? __exception)
        {
            if (__state != null) MonMPPlugin.Instance?.World?.BoatMotion?.EndNativeEngineUpdate(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Part_FuelTank), "set_Get_FuelLevel")]
    [HarmonyPriority(Priority.First)]
    internal static class BoatGuestFuelBurnPatch
    {
        private static bool Prefix(Part_FuelTank __instance) =>
            MonMPPlugin.Instance?.World?.BoatMotion?.AllowNativeFuelWrite(__instance) != false;
    }

    [HarmonyPatch(typeof(AdvancedShipController), nameof(AdvancedShipController.SetInputToPlayer))]
    [HarmonyPriority(Priority.First)]
    internal static class SharedBoatDriverInputOwnershipPatch
    {
        private static bool Prefix(AdvancedShipController __instance, bool _active) =>
            MonMPPlugin.Instance?.World?.BoatMotion?.SetInputToPlayer(__instance, _active) != false;
    }

    [HarmonyPatch(typeof(AdvancedShipController), nameof(AdvancedShipController.Update))]
    internal static class SharedBoatNativeInputPatch
    {
        private static void Postfix(AdvancedShipController __instance) =>
            MonMPPlugin.Instance?.World?.BoatMotion?.SampleNativeInputs(__instance);
    }

    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.FixedUpdate))]
    internal static class SharedBoatPresentedVelocityPatch
    {
        private static readonly Action<Vehicle, Vector3>? SetVelocity = Setter<Vector3>(nameof(Vehicle.Velocity));
        private static readonly Action<Vehicle, Vector3>? SetLocalVelocity = Setter<Vector3>(nameof(Vehicle.LocalVelocity));
        private static readonly Action<Vehicle, float>? SetLocalForward = Setter<float>(nameof(Vehicle.LocalForwardVelocity));
        private static readonly Action<Vehicle, float>? SetMagnitude = Setter<float>(nameof(Vehicle.VelocityMagnitude));

        private static Action<Vehicle, T>? Setter<T>(string property)
        {
            var setter = AccessTools.PropertySetter(typeof(Vehicle), property);
            return setter != null ? AccessTools.MethodDelegate<Action<Vehicle, T>>(setter) : null;
        }

        private static void Postfix(Vehicle __instance)
        {
            if (__instance is not AdvancedShipController || SetVelocity == null || SetLocalVelocity == null ||
                SetLocalForward == null || SetMagnitude == null ||
                MonMPPlugin.Instance?.World?.BoatMotion?.TryPresentedVelocity(__instance, out var velocity) != true) return;
            var local = __instance.transform.InverseTransformDirection(velocity);
            SetVelocity(__instance, velocity);
            SetLocalVelocity(__instance, local);
            SetLocalForward(__instance, local.z);
            SetMagnitude(__instance, velocity.magnitude);
        }
    }
}
