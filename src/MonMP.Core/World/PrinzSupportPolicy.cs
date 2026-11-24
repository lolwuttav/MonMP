using System;

namespace MonMP.Core.World
{
    /// <summary>
    /// Stable optional-entity identities for the canonical native Prinz. Unlike the Main
    /// Konig's long-lived save-backed IDs, these records are namespaced by native vehicle type
    /// and allocated from the optional dynamic entity range.
    /// </summary>
    public static class PrinzSupportPolicy
    {
        public static string VehicleIdentity(string canonicalVehiclePath)
        {
            if (string.IsNullOrWhiteSpace(canonicalVehiclePath))
                throw new ArgumentException("A canonical native vehicle path is required.", nameof(canonicalVehiclePath));
            return "native-vehicle-v1/Prinz/vehicle-v1/" + canonicalVehiclePath;
        }

        public static string PartSocketIdentity(string canonicalSocketPath)
        {
            if (string.IsNullOrWhiteSpace(canonicalSocketPath))
                throw new ArgumentException("A canonical native part-socket path is required.", nameof(canonicalSocketPath));
            return "native-vehicle-v1/Prinz/part-socket-v1/" + canonicalSocketPath;
        }

        public static ulong DynamicEntityId(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
                throw new ArgumentException("A canonical dynamic entity identity is required.", nameof(identity));
            ulong id = StaticId.Hash(identity) | WorldRecord.DynamicIdStart;
            return id == WorldRecord.DynamicIdStart ? id + 1 : id;
        }

        public static ulong VehicleEntityId(string canonicalVehiclePath) =>
            DynamicEntityId(VehicleIdentity(canonicalVehiclePath));

        public static ulong PartSocketEntityId(string canonicalSocketPath) =>
            DynamicEntityId(PartSocketIdentity(canonicalSocketPath));
    }
}
