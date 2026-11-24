namespace MonMP.Core.World
{
    public static class VehicleSeatDoorPolicy
    {
        public static bool Allows(bool bus, bool doorless, bool driver, bool driverClosed, bool passengerClosed) =>
            bus || doorless || (driver ? !driverClosed || !passengerClosed : !passengerClosed);

        // Gameplay exits the current seat through either open side; the role of
        // the seat being clicked does not choose or restrict that exit.
        public static bool AllowsExit(bool bus, bool doorless, bool driverClosed, bool passengerClosed) =>
            bus || doorless || !driverClosed || !passengerClosed;
    }
}
