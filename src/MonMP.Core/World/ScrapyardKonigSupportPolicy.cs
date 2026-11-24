using System;

namespace MonMP.Core.World
{
    /// <summary>
    /// Shape gate for the native scrapyard-Konig chassis. Activity and seat installation are
    /// mutable scene state; they do not change the identity or validity of a loaded chassis.
    /// </summary>
    public readonly struct ScrapyardKonigMotionShape
    {
        public readonly bool IsCanonicalNativeVehicle;
        public readonly bool IsInLoadedScene;
        public readonly bool ControllerEnabled;
        public readonly bool RootBodyDynamic;
        public readonly bool HasFuelTank;
        public readonly int ControllerCount;
        public readonly int RigidbodyCount;
        public readonly int WheelCount;
        public readonly int WheelVisualCount;

        public ScrapyardKonigMotionShape(bool isCanonicalNativeVehicle, bool isInLoadedScene,
            bool controllerEnabled, bool rootBodyDynamic, bool hasFuelTank, int controllerCount,
            int rigidbodyCount, int wheelCount, int wheelVisualCount)
        {
            IsCanonicalNativeVehicle = isCanonicalNativeVehicle;
            IsInLoadedScene = isInLoadedScene;
            ControllerEnabled = controllerEnabled;
            RootBodyDynamic = rootBodyDynamic;
            HasFuelTank = hasFuelTank;
            ControllerCount = controllerCount;
            RigidbodyCount = rigidbodyCount;
            WheelCount = wheelCount;
            WheelVisualCount = wheelVisualCount;
        }
    }

    public readonly struct ScrapyardKonigDriverSeatShape
    {
        public readonly int SupportedDriverCount;
        public readonly bool DriverBelongsToController;
        public readonly bool DriverPositionActive;

        public ScrapyardKonigDriverSeatShape(int supportedDriverCount, bool driverBelongsToController,
            bool driverPositionActive)
        {
            SupportedDriverCount = supportedDriverCount;
            DriverBelongsToController = driverBelongsToController;
            DriverPositionActive = driverPositionActive;
        }
    }

    public static class ScrapyardKonigSupportPolicy
    {
        public static bool SupportsMotion(ScrapyardKonigMotionShape shape) =>
            shape.IsCanonicalNativeVehicle && shape.IsInLoadedScene && shape.ControllerEnabled &&
            shape.RootBodyDynamic && shape.HasFuelTank && shape.ControllerCount == 1 &&
            shape.RigidbodyCount == 1 && shape.WheelCount == 4 && shape.WheelVisualCount == 4;

        // Seat installation is mutable gameplay state. It controls who may enter the vehicle,
        // but must not control whether the native chassis itself has a shared motion record.
        public static bool SupportsDriverSeat(ScrapyardKonigDriverSeatShape shape) =>
            shape.SupportedDriverCount == 1 && shape.DriverBelongsToController && shape.DriverPositionActive;

        public static string VehicleIdentity(string canonicalVehiclePath)
        {
            if (string.IsNullOrWhiteSpace(canonicalVehiclePath)) throw new ArgumentException("A canonical native vehicle path is required.", nameof(canonicalVehiclePath));
            return "native-vehicle-v2/ScrapyardKonig/vehicle-v1/" + canonicalVehiclePath;
        }

        public static string PartSocketIdentity(string canonicalSocketPath)
        {
            if (string.IsNullOrWhiteSpace(canonicalSocketPath)) throw new ArgumentException("A canonical native part-socket path is required.", nameof(canonicalSocketPath));
            return "native-vehicle-v1/ScrapyardKonig/part-socket-v1/" + canonicalSocketPath;
        }

        public static ulong DynamicEntityId(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A canonical dynamic entity identity is required.", nameof(identity));
            ulong id = StaticId.Hash(identity) | WorldRecord.DynamicIdStart;
            return id == WorldRecord.DynamicIdStart ? id + 1 : id;
        }

        public static ulong VehicleEntityId(string canonicalVehiclePath) =>
            DynamicEntityId(VehicleIdentity(canonicalVehiclePath));

        public static ulong PartSocketEntityId(string canonicalSocketPath) =>
            DynamicEntityId(PartSocketIdentity(canonicalSocketPath));
    }
}
