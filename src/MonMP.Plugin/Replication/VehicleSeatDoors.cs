using MonMP.Core.World;
using NWH;

namespace MonMP.Plugin.Replication
{
    internal static class VehicleSeatDoors
    {
        internal static bool Allows(Vehicle? vehicle, bool driver) => vehicle == null || VehicleSeatDoorPolicy.Allows(
            MonMPPlugin.Instance?.World?.SchoolBusPassengers.IsCurrentVehicle(vehicle) == true,
            vehicle.NoDriverDoor, driver, vehicle.isDriverDoorClosed, vehicle.isPassengerDoorClosed);

        internal static bool AllowsExit(Vehicle? vehicle) => vehicle == null || VehicleSeatDoorPolicy.AllowsExit(
            MonMPPlugin.Instance?.World?.SchoolBusPassengers.IsCurrentVehicle(vehicle) == true,
            vehicle.NoDriverDoor, vehicle.isDriverDoorClosed, vehicle.isPassengerDoorClosed);

        internal static bool AllowsTransition(Gameplay gameplay, Vehicle? vehicle, bool driver) =>
            gameplay.Get_CurrentVehicleController == vehicle &&
            (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver || gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)
                ? AllowsExit(vehicle) : Allows(vehicle, driver);
    }
}
