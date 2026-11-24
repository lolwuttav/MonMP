namespace MonMP.Core.World
{
    /// <summary>
    /// Shape gates shared by the native Scrapyard Konig and Prinz shell adapters. An inactive
    /// or not-yet-started canonical shell can be catalogued dormant; promotion still requires
    /// the complete, active, supported chassis shape.
    /// </summary>
    public readonly struct NativeKonigShellMotionShape
    {
        public readonly bool IsCanonicalVehicle;
        public readonly bool IsInLoadedScene;
        public readonly bool RootActive;
        public readonly bool ControllerEnabled;
        public readonly bool RootBodyPresent;
        public readonly bool RootBodyDynamic;
        public readonly bool HasFuelTank;
        public readonly bool HasSupportedSavedHinges;
        public readonly bool ProgressionLocked;
        public readonly int ControllerCount;
        public readonly int RigidbodyCount;
        public readonly int WheelCount;
        public readonly int WheelVisualCount;

        public NativeKonigShellMotionShape(bool isCanonicalVehicle, bool isInLoadedScene, bool rootActive,
            bool controllerEnabled, bool rootBodyPresent, bool rootBodyDynamic, bool hasFuelTank,
            bool hasSupportedSavedHinges, bool progressionLocked, int controllerCount,
            int rigidbodyCount, int wheelCount, int wheelVisualCount)
        {
            IsCanonicalVehicle = isCanonicalVehicle;
            IsInLoadedScene = isInLoadedScene;
            RootActive = rootActive;
            ControllerEnabled = controllerEnabled;
            RootBodyPresent = rootBodyPresent;
            RootBodyDynamic = rootBodyDynamic;
            HasFuelTank = hasFuelTank;
            HasSupportedSavedHinges = hasSupportedSavedHinges;
            ProgressionLocked = progressionLocked;
            ControllerCount = controllerCount;
            RigidbodyCount = rigidbodyCount;
            WheelCount = wheelCount;
            WheelVisualCount = wheelVisualCount;
        }
    }

    public readonly struct NativeKonigShellSeatShape
    {
        public readonly int DriverSeatCount;
        public readonly int PassengerSeatCount;
        public readonly bool DriverBelongsToVehicle;
        public readonly bool PassengerBelongsToVehicle;
        public readonly bool DriverPositionPresent;
        public readonly bool PassengerPositionPresent;
        public readonly int SeatCustomParameterCount;
        public readonly bool DriverPartInstalled;
        public readonly bool AdjustmentControlsReady;

        public NativeKonigShellSeatShape(int driverSeatCount, int passengerSeatCount,
            bool driverBelongsToVehicle, bool passengerBelongsToVehicle,
            bool driverPositionPresent, bool passengerPositionPresent, int seatCustomParameterCount,
            bool driverPartInstalled = true, bool adjustmentControlsReady = true)
        {
            DriverSeatCount = driverSeatCount;
            PassengerSeatCount = passengerSeatCount;
            DriverBelongsToVehicle = driverBelongsToVehicle;
            PassengerBelongsToVehicle = passengerBelongsToVehicle;
            DriverPositionPresent = driverPositionPresent;
            PassengerPositionPresent = passengerPositionPresent;
            SeatCustomParameterCount = seatCustomParameterCount;
            DriverPartInstalled = driverPartInstalled;
            AdjustmentControlsReady = adjustmentControlsReady;
        }
    }

    public static class NativeKonigShellSupportPolicy
    {
        public static bool CanDeferActivation(NativeKonigShellMotionShape shape) =>
            shape.IsCanonicalVehicle && shape.IsInLoadedScene && shape.ControllerCount == 1 &&
            // Once a root rigidbody cache exists, even a dormant shell must prove its complete
            // known hinge-body topology. A genuinely uninitialized root cache may wait untouched
            // for native startup, but it is checked again before promotion.
            (!shape.RootBodyPresent || (shape.HasSupportedSavedHinges && shape.RigidbodyCount == 1)) &&
            (!shape.RootActive || !shape.ControllerEnabled || !shape.RootBodyPresent ||
                shape.WheelCount != 4 || shape.WheelVisualCount != 4 ||
                (shape.ProgressionLocked && !shape.RootBodyDynamic));

        // Every condition remains mandatory when promoting. In particular, callers may report one
        // validated native hood/trunk/door hinge body as the normalized single supported body only
        // after HasSupportedSavedHinges has independently proven the exact native hinge shape.
        public static bool SupportsMotion(NativeKonigShellMotionShape shape) =>
            shape.IsCanonicalVehicle && shape.IsInLoadedScene && shape.RootActive && shape.ControllerEnabled &&
            shape.RootBodyPresent && shape.RootBodyDynamic && shape.HasFuelTank && shape.HasSupportedSavedHinges &&
            shape.ControllerCount == 1 && shape.RigidbodyCount == 1 && shape.WheelCount == 4 && shape.WheelVisualCount == 4;

        // These structural seat records do not depend on removable Part_Seat installation or on
        // adjustment buttons being active; native solidity and a late-bound control gate actual use.
        public static bool SupportsPassengerRecords(NativeKonigShellSeatShape shape) =>
            shape.DriverSeatCount == 1 && shape.PassengerSeatCount == 1 &&
            shape.DriverBelongsToVehicle && shape.PassengerBelongsToVehicle &&
            shape.DriverPositionPresent && shape.PassengerPositionPresent;

        public static bool SupportsSeatAdjustmentRecord(NativeKonigShellSeatShape shape) =>
            shape.DriverSeatCount == 1 && shape.DriverBelongsToVehicle && shape.DriverPositionPresent &&
            shape.SeatCustomParameterCount >= 2;

        public static bool SupportsPassengerSeatAdjustmentRecord(NativeKonigShellSeatShape shape) =>
            shape.PassengerSeatCount == 1 && shape.PassengerBelongsToVehicle && shape.PassengerPositionPresent &&
            shape.SeatCustomParameterCount >= 2;
    }
}
