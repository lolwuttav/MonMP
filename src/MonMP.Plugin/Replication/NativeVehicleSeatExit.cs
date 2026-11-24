using HarmonyLib;
using NWH;

namespace MonMP.Plugin.Replication
{
    internal static class NativeVehicleSeatExit
    {
        // Explicit seat clicks use the same open-side selection as native
        // EnterExitVehicle. Forced disconnect/revocation still uses Force_ExitVehicle.
        internal static bool DriverSide(Vehicle vehicle, bool driver) =>
            driver ? !vehicle.isDriverDoorClosed : vehicle.isPassengerDoorClosed;

        internal static void Exit(Gameplay gameplay, Vehicle vehicle, bool driverSide)
        {
            var exit = driverSide ? vehicle.ExitVehicle_Driver : vehicle.ExitVehicle_Passenger;
            gameplay.PlayerWalking.transform.localPosition = exit.localPosition;
            AccessTools.Method(typeof(Gameplay), "ExitVehicle").Invoke(gameplay, null);
        }
    }
}
