using NWH;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Only Gameplay.Tick uses this read. A kinematic replica can have a zero/stale
    // physics speed between fixed updates even though a fresh host sample is moving.
    internal static class PlayerAnxietyRelief
    {
        internal static float VehicleSpeed(Vehicle vehicle)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            if (world == null || world.IsDisposed || plugin?.Session?.IsRunning != true) return vehicle.Speed;
            if (world.Vehicles.TryPresentedVelocity(vehicle, out var velocity) ||
                world.BoatMotion.TryPresentedVelocity(vehicle, out velocity) ||
                world.SchoolBus.TryPresentedVelocity(vehicle, out velocity) ||
                world.MartinCar.TryPresentedVelocity(vehicle, out velocity))
                return Mathf.Abs(Vector3.Dot(vehicle.transform.forward, velocity));
            return vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote ? 0f : vehicle.Speed;
        }

        internal static void AddedPassengerTick(Gameplay gameplay)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            // Native driver/passenger postures already receive exactly one reduction.
            // Added cab/bed/ATV/bus seats use the native chair posture instead.
            if (world == null || world.IsDisposed || plugin?.Session?.IsRunning != true ||
                gameplay.Get_PlayerState != PlayerState.InFurnitureChair) return;
            var vehicle = world.ExtraSeats.CurrentLocalPassengerVehicle;
            var needs = gameplay.GetComponent<BasicNeeds_Manager>();
            if (vehicle == null || needs == null || needs.Dead) return;
            float speed = VehicleSpeed(vehicle);
            if (speed > 1f) needs.Fill_Anxiety(Mathf.Clamp01(speed / 6f));
        }
    }
}
