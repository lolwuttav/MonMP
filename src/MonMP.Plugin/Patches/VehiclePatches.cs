using System.Collections.Generic;
using System.Collections;
using System;
using System.Reflection;
using HarmonyLib;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Trailer;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // #164: a vehicle switch or lever click resets the hovered interactable, which hides the prompt for a
    // frame and re-shows it on the next raycast (a visible flicker on every state change, including the
    // guest's queued prompt refreshes). A still-hovered, usable vehicle control refreshes in place instead.
    [HarmonyPatch(typeof(PlayerInteractable), nameof(PlayerInteractable.ResetCurrentInteractable))]
    internal static class VehicleControlPromptRefreshPatch
    {
        private static bool Prefix(PlayerInteractable __instance, Interactable OnlyIfItsThisOne)
        {
            if (OnlyIfItsThisOne == null || __instance.Get_CurrentInteractable != OnlyIfItsThisOne || !OnlyIfItsThisOne.isActiveAndEnabled) return true;
            if (!(OnlyIfItsThisOne is Vehicle_IgnitionSwitch || OnlyIfItsThisOne is Vehicle_HighLowShifter ||
                OnlyIfItsThisOne.GetType().Name.StartsWith("Vehicle_Button_", StringComparison.Ordinal)) || !OnlyIfItsThisOne.CanUse()) return true;
            OnlyIfItsThisOne.MouseHover();
            return false;
        }
    }

    [HarmonyPatch(typeof(Vehicle_Button_LightDomeOptions), "ChangeLightMode")]
    internal static class VehicleDomeModePatch
    {
        private static readonly FieldInfo Target = AccessTools.Field(typeof(Vehicle_Button_LightDomeOptions), "vc");
        private static bool Prefix(Vehicle_Button_LightDomeOptions __instance)
        {
            var shared = MonMPPlugin.Instance?.Vehicles;
            if (shared == null) return true;
            if (shared.RequestDomeMode(__instance)) return false;
            var vehicle = (Vehicle?)Target.GetValue(__instance);
            return vehicle != null && shared.Handles(vehicle) && !shared.TransfersBlocked && shared.CanControl(vehicle);
        }
    }
    [HarmonyPatch(typeof(Vehicle_Button_WindowSwitch), "MovingWindow")]
    internal static class SharedWindowMotionPatch
    {
        private static void Postfix(Vehicle_Button_WindowSwitch __instance, ref IEnumerator __result)
            => __result = SuspendReplica(__instance, __result);
        private static IEnumerator SuspendReplica(Vehicle_Button_WindowSwitch button, IEnumerator native)
        {
            try
            {
                while (true)
                {
                    if (MonMPPlugin.Instance?.Vehicles?.Windows.IsReplica(button) == true) { yield return null; continue; }
                    if (!native.MoveNext()) yield break;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }
    [HarmonyPatch(typeof(Vehicle_Button_WindowSwitch), "MouseDown")]
    internal static class SharedWindowClickPatch
    {
        private static bool Prefix(Vehicle_Button_WindowSwitch __instance) => MonMPPlugin.Instance?.Vehicles?.Windows.Click(__instance) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Door), "ToggleDoor")]
    internal static class SharedDoorTogglePatch
    {
        private static bool Prefix(Vehicle_Door __instance) => MonMPPlugin.Instance?.Vehicles?.Doors.Toggle(__instance) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Door), "FixedUpdate")]
    internal static class SharedDoorPhysicsPatch
    {
        private static bool Prefix(Vehicle_Door __instance) => MonMPPlugin.Instance?.Vehicles?.Doors.NativePhysics(__instance) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Hood), "Release")]
    internal static class SharedHoodReleasePatch
    {
        private static bool Prefix(Vehicle_Hood __instance) => MonMPPlugin.Instance?.Vehicles?.Hood.Trigger(__instance, 0) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Hood), "Open")]
    internal static class SharedHoodOpenPatch
    {
        private static bool Prefix(Vehicle_Hood __instance) => MonMPPlugin.Instance?.Vehicles?.Hood.Trigger(__instance, 1) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Hood), "Close")]
    internal static class SharedHoodClosePatch
    {
        private static bool Prefix(Vehicle_Hood __instance) => MonMPPlugin.Instance?.Vehicles?.Hood.Trigger(__instance, 2) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Hood), "FixedUpdate")]
    internal static class SharedHoodPhysicsPatch
    {
        private static bool Prefix(Vehicle_Hood __instance) => MonMPPlugin.Instance?.Vehicles?.Hood.NativePhysics(__instance) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Trunk), "Release")]
    internal static class SharedTrunkTogglePatch
    {
        private static bool Prefix(Vehicle_Trunk __instance) => MonMPPlugin.Instance?.Vehicles?.Trunk.Toggle(__instance) != false;
    }
    [HarmonyPatch(typeof(Vehicle_Trunk), "FixedUpdate")]
    internal static class SharedTrunkPhysicsPatch
    {
        private static bool Prefix(Vehicle_Trunk __instance) => MonMPPlugin.Instance?.Vehicles?.Trunk.NativePhysics(__instance) != false;
    }
    [HarmonyPatch(typeof(Gameplay), "EnterExitVehicle")]
    internal static class VehicleSeatPatch
    {
        private static bool Prefix(Gameplay __instance, Vehicle _vehicleController, Transform _seatPosition, bool _isDriverSeat)
        {
            var world = MonMPPlugin.Instance?.World;
            var vehicles = world?.Vehicles;
            var busPassengers = world?.SchoolBusPassengers;
            if (vehicles == null) return true;
            var boatMotion = world?.BoatMotion;
            if (boatMotion?.Handles(_vehicleController) == true)
                return boatMotion.EnterExit(_vehicleController, _seatPosition, _isDriverSeat);
            if (vehicles.Applying || busPassengers?.Applying == true || world?.MartinCar.Applying == true) return true;
            if (!Replication.VehicleSeatDoors.AllowsTransition(__instance, _vehicleController, _isDriverSeat)) return false;
            if (busPassengers?.IsCurrentVehicle(__instance.Get_CurrentVehicleController) == true &&
                __instance.Get_CurrentVehicleController != _vehicleController) return false;
            if (vehicles.Handles(__instance.Get_CurrentVehicleController) && __instance.Get_CurrentVehicleController != _vehicleController) return false;
            if (world?.MartinCar.Handles(__instance.Get_CurrentVehicleController) == true && __instance.Get_CurrentVehicleController != _vehicleController) return false;
            if (world?.MartinCar.Handles(_vehicleController) == true)
                return world.MartinCar.EnterExit(_vehicleController, _seatPosition, _isDriverSeat);
            if (busPassengers?.HandlesSeat(_vehicleController, _seatPosition, _isDriverSeat) == true ||
                busPassengers?.IsCurrentVehicle(_vehicleController as VehicleController) == true)
                return busPassengers!.EnterExit(_vehicleController, _seatPosition, _isDriverSeat);
            return vehicles.EnterExit(_vehicleController, _seatPosition, _isDriverSeat);
        }
    }
    [HarmonyPatch(typeof(Part_Seat_Forward), nameof(Part_Seat_Forward.MouseDown))]
    internal static class KonigSeatForwardIncreasePatch
    {
        private static bool Prefix(Part_Seat_Forward __instance) =>
            MonMPPlugin.Instance?.Vehicles?.SeatAdjustmentButton(__instance, MonMP.Core.World.VehicleSeatAdjustmentAxis.Forward, 1) != false;
        private static void Postfix(Part_Seat_Forward __instance) => MonMPPlugin.Instance?.Vehicles?.NativeSeatAdjustmentButton(__instance);
    }
    [HarmonyPatch(typeof(Part_Seat_Forward), nameof(Part_Seat_Forward.MouseRightDown))]
    internal static class KonigSeatForwardDecreasePatch
    {
        private static bool Prefix(Part_Seat_Forward __instance) =>
            MonMPPlugin.Instance?.Vehicles?.SeatAdjustmentButton(__instance, MonMP.Core.World.VehicleSeatAdjustmentAxis.Forward, -1) != false;
        private static void Postfix(Part_Seat_Forward __instance) => MonMPPlugin.Instance?.Vehicles?.NativeSeatAdjustmentButton(__instance);
    }
    [HarmonyPatch(typeof(Part_Seat_Height), nameof(Part_Seat_Height.MouseDown))]
    internal static class KonigSeatHeightIncreasePatch
    {
        private static bool Prefix(Part_Seat_Height __instance) =>
            MonMPPlugin.Instance?.Vehicles?.SeatAdjustmentButton(__instance, MonMP.Core.World.VehicleSeatAdjustmentAxis.Height, 1) != false;
        private static void Postfix(Part_Seat_Height __instance) => MonMPPlugin.Instance?.Vehicles?.NativeSeatAdjustmentButton(__instance);
    }
    [HarmonyPatch(typeof(Part_Seat_Height), nameof(Part_Seat_Height.MouseRightDown))]
    internal static class KonigSeatHeightDecreasePatch
    {
        private static bool Prefix(Part_Seat_Height __instance) =>
            MonMPPlugin.Instance?.Vehicles?.SeatAdjustmentButton(__instance, MonMP.Core.World.VehicleSeatAdjustmentAxis.Height, -1) != false;
        private static void Postfix(Part_Seat_Height __instance) => MonMPPlugin.Instance?.Vehicles?.NativeSeatAdjustmentButton(__instance);
    }
    [HarmonyPatch(typeof(Gameplay), "ExitVehicle")]
    internal static class VehicleExitPatch
    {
        private static void Postfix()
        {
            MonMPPlugin.Instance?.Vehicles?.NativeExited();
            MonMPPlugin.Instance?.World?.SchoolBusPassengers.NativeExited();
            MonMPPlugin.Instance?.World?.MartinCar.NativeExited();
            MonMPPlugin.Instance?.World?.BoatMotion.NativeExited();
        }
    }
    [HarmonyPatch]
    internal static class VehicleControlsPatch
    {
        private static readonly FieldInfo ExtraVehicle = AccessTools.Field(typeof(Vehicle_Button_ExtraLights), "vehicleController");
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(Vehicle_IgnitionSwitch), typeof(Vehicle_HighLowShifter), typeof(Vehicle_Button_ParkingBrake), typeof(Vehicle_Button_Shifter), typeof(Vehicle_Button_LightSwitch), typeof(Vehicle_Button_Highbeam), typeof(Vehicle_Button_HazardLights), typeof(Vehicle_Button_ExtraLights) })
                yield return AccessTools.Method(type, "MouseDown");
            yield return AccessTools.Method(typeof(Vehicle_Button_LightSwitch), "MouseRightDown");
            yield return AccessTools.Method(typeof(Vehicle_Button_Shifter), "MouseRightDown");
            yield return AccessTools.Method(typeof(Vehicle_Button_ParkingBrake), "MouseRightDown");
            yield return AccessTools.Method(typeof(Vehicle_Button_ParkingBrake), "Use");
            yield return AccessTools.Method(typeof(Vehicle_IgnitionSwitch), "MouseUp");
        }
        private static bool Prefix(Component __instance, MethodBase __originalMethod)
        {
            // Boat keys keep their own MouseUp route (SharedBoatControls).
            if (__instance is Vehicle_IgnitionSwitch_FishingBoat && __originalMethod.Name == "MouseUp") return true;
            var shared = MonMPPlugin.Instance?.Vehicles;
            if (shared == null) return true;
            if (shared.RequestControl(__instance, __originalMethod.Name)) return false;
            Vehicle? lighting = __instance is Vehicle_Button_LightSwitch low ? low.vehicleController :
                __instance is Vehicle_Button_Highbeam high ? high.vehicleController :
                __instance is Vehicle_Button_HazardLights hazard ? hazard.vehicleController :
                __instance is Vehicle_Button_ExtraLights extra ? (Vehicle?)ExtraVehicle.GetValue(extra) : null;
            if (lighting != null) return shared.Handles(lighting) && !shared.TransfersBlocked && shared.CanControl(lighting);
            var vehicle = __instance.GetComponentInParent<Vehicle>();
            if (vehicle == null) return true;
            bool supportedBoat = vehicle.vehicle_type == Vehicle_Type.FishingBoat || vehicle.vehicle_type == Vehicle_Type.SmallCruiser;
            if (supportedBoat && MonMPPlugin.Instance?.BoatControls?.CanSetIgnition(vehicle) == false) return false;
            return shared.CanControl(vehicle);
        }
    }
    // #250: a remote-presented vehicle's native speed comes from its kinematic body's velocity, which
    // stays zero while presentation moves it; the surface dust (and other speed-driven effects) read it.
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.FixedUpdate))]
    internal static class RemoteVehicleVelocityPatch
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
            if (__instance.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote && __instance.vehicleRigidbody?.isKinematic != true) return;
            if (SetVelocity == null || SetLocalVelocity == null || SetLocalForward == null || SetMagnitude == null) return;
            if (MonMPPlugin.Instance?.Vehicles?.TryPresentedVelocity(__instance, out var velocity) != true &&
                MonMPPlugin.Instance?.World?.SchoolBus.TryPresentedVelocity(__instance, out velocity) != true) return;
            var local = __instance.transform.InverseTransformDirection(velocity);
            SetVelocity(__instance, velocity); SetLocalVelocity(__instance, local);
            SetLocalForward(__instance, local.z); SetMagnitude(__instance, velocity.magnitude);
        }
    }
    [HarmonyPatch(typeof(Vehicle), "set_Get_IgnitionPosition")]
    internal static class VehicleIgnitionPatch
    {
        private static bool Prefix(Vehicle __instance) => MonMPPlugin.Instance?.BoatControls?.CanSetIgnition(__instance) != false &&
            MonMPPlugin.Instance?.Vehicles?.ApplyingMechanics != true && MonMPPlugin.Instance?.Vehicles?.CanControl(__instance) != false;
        private static void Postfix(Vehicle __instance) => MonMPPlugin.Instance?.BoatControls?.IgnitionChanged(__instance);
    }
    [HarmonyPatch(typeof(TrailerHitchModule), "AttachTrailer")]
    internal static class SharedTrailerAttachPatch
    {
        private static bool Prefix(VehicleController vc)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return plugin.World?.Trailers.ApplyingNative == true;
        }
    }
    [HarmonyPatch(typeof(TrailerHitchModule), "DetachTrailer")]
    internal static class SharedTrailerDetachPatch
    {
        private static bool Prefix(VehicleController vc)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return plugin.World?.Trailers.ApplyingNative == true;
        }
    }
    [HarmonyPatch(typeof(Trailer_Coupler), "MouseDown")]
    internal static class SharedTrailerCouplerPatch
    {
        private static bool Prefix(Trailer_Coupler __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return plugin.World?.Trailers.Interact(__instance) == true;
        }
    }
}
