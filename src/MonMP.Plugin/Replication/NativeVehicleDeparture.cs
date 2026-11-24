using System;
using MonMP.Core.World;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeVehicleDeparture
    {
        // Gameplay.Force_ExitVehicle assigns the exit's localPosition to a walker
        // parented directly to the vehicle, then unparents it. Match that root exactly.
        internal static bool TryExit(Vehicle? vehicle, bool driver, out Vector3 position)
        {
            position = default;
            if (vehicle == null) return false;
            var exit = driver ? vehicle.ExitVehicle_Driver : vehicle.ExitVehicle_Passenger;
            if (exit == null) return false;
            position = vehicle.transform.TransformPoint(exit.localPosition);
            return Valid(position);
        }

        internal static bool TryExit(Vehicle? vehicle, bool driver, ItemPose root, out Vector3 position)
        {
            position = default;
            if (vehicle == null || !TryExit(vehicle, driver, out var currentExit)) return false;
            // Remote presentation can lag the reliable final driver frame. Keep the
            // native scaled exit offset, but place it at that accepted authoritative pose.
            var offset = Quaternion.Inverse(vehicle.transform.rotation) * (currentExit - vehicle.transform.position);
            var rotation = new Quaternion(root.Qx, root.Qy, root.Qz, root.Qw);
            position = new Vector3(root.X, root.Y, root.Z) + rotation * offset;
            return Valid(position);
        }

        private static bool Valid(Vector3 position) => float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z) &&
            Math.Abs(position.x) <= 100000f && Math.Abs(position.y) <= 100000f && Math.Abs(position.z) <= 100000f;

        internal static void SetParkingBrake(VehicleController vehicle, Vehicle_Button_ParkingBrake? parking)
        {
            // The native load setter updates the latched brake and its visual without
            // replaying a click/sound. Brakes.FixedUpdate reads Get_IsOn every step.
            if (parking != null && parking.LoadFromSave == 0) parking.LoadFromSave = 1;
            vehicle.input.states.throttle = 0;
            vehicle.input.states.steering = 0;
            vehicle.input.states.brakes = 0;
            vehicle.input.states.handbrake = 1;
            vehicle.input.states.engineStartStop = false;
            vehicle.input.states.boost = false;
            vehicle.input.states.horn = false;
            vehicle.brakes.handbrakeValue = 1;
        }

        internal static VehicleState Parked(VehicleState state)
        {
            return new VehicleState(state.Driver, state.Pose, state.Velocity, state.AngularVelocity, state.Fuel,
                state.EngineAngularVelocity, 0, 0, 0, 1, state.Gear, state.Ignition, state.EngineIgnition,
                false, state.ParkingBrake == 2 ? 2 : 1, state.TransferCase, state.Accessory, state.Lighting,
                state.WheelSpin, state.WheelAngles, state.Effects, 1, state.AutomaticPark, state.Active, state.MotionReady, state.RefuelCredit, state.LiftFrozen);
        }
    }
}
