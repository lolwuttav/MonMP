using System;
using MonMP.Core.World;
using NWH.VehiclePhysics2;

namespace MonMP.Plugin.Replication
{
    // Canonical fleet admission and optional identity policy shared by part sockets and bolt holes.
    // Keep the native Prinz identity separate from Konig/ScrapyardKonig even though several parts
    // share prefab families: native compatibility is still checked against ModifiableVehicles.Prinz.
    internal static class SharedVehiclePartIdentityPolicy
    {
        internal static readonly Vehicle_Type[] Fleet =
        {
            Vehicle_Type.MainVehicle, Vehicle_Type.ScrapyardKonig, Vehicle_Type.OlTruck,
            Vehicle_Type.SmollATV, Vehicle_Type.Buggy, Vehicle_Type.Prinz
        };

        internal static VehicleController? CanonicalPrinzVehicle(GlobalClassManager? global)
        {
            if (global == null || global.GetVehicles(Vehicle_Type.Prinz) is not VehicleController prinz ||
                prinz == null || !prinz.gameObject.scene.IsValid()) return null;
            return prinz;
        }

        internal static bool IsCanonicalPrinzVehicle(GlobalClassManager? global, VehicleController? candidate) =>
            candidate != null && CanonicalPrinzVehicle(global) == candidate;

        internal static ModifiableVehicles ModifiableVehicle(Vehicle_Type type) => type switch
        {
            Vehicle_Type.MainVehicle or Vehicle_Type.ScrapyardKonig => ModifiableVehicles.Konig,
            Vehicle_Type.Prinz => ModifiableVehicles.Prinz,
            Vehicle_Type.OlTruck => ModifiableVehicles.OlTruck,
            _ => ModifiableVehicles.None
        };

        internal static bool IsSteeringRackBinding(Vehicle_Type type, Car_PartsType socketType) =>
            socketType == Car_PartsType.SteeringRack &&
            type is Vehicle_Type.MainVehicle or Vehicle_Type.ScrapyardKonig or Vehicle_Type.Prinz;

        internal static bool IsCompatiblePart(Vehicle_Type type, Car_PartsType socketType, InteractableParts? part)
        {
            if (part == null || part.car_PartsType != socketType) return false;
            ModifiableVehicles required = ModifiableVehicle(type);
            return required == ModifiableVehicles.None || (part.CompatibleVehiclesType & required) != ModifiableVehicles.None;
        }

        internal static string PartSocketIdentity(Vehicle_Type type, string canonicalPath) => type switch
        {
            Vehicle_Type.ScrapyardKonig => ScrapyardKonigSupportPolicy.PartSocketIdentity(canonicalPath),
            Vehicle_Type.Prinz => PrinzSupportPolicy.PartSocketIdentity(RequiredPath(canonicalPath)),
            _ => RequiredPath(canonicalPath) + "/part-socket-v1"
        };

        internal static ulong PartSocketEntityId(Vehicle_Type type, string canonicalPath)
        {
            string identity = PartSocketIdentity(type, canonicalPath);
            return type == Vehicle_Type.ScrapyardKonig
                ? ScrapyardKonigSupportPolicy.DynamicEntityId(identity)
                : type == Vehicle_Type.Prinz ? PrinzSupportPolicy.DynamicEntityId(identity) : StaticId.Hash(identity);
        }

        internal static string BoltHoleIdentity(Vehicle_Type type, string canonicalSocketPath, int holeIndex)
        {
            string socketPath = RequiredPath(canonicalSocketPath);
            return type == Vehicle_Type.Prinz
                ? PrinzSupportPolicy.PartSocketIdentity(socketPath) + "/bolt-hole-v1/bolt-" + holeIndex + "-v1"
                : socketPath + "/bolt-" + holeIndex + "-v1";
        }

        internal static ulong BoltHoleEntityId(Vehicle_Type type, string identity) => type switch
        {
            Vehicle_Type.ScrapyardKonig => ScrapyardKonigSupportPolicy.DynamicEntityId("vehicle-bolt-hole-v1/" + RequiredPath(identity)),
            Vehicle_Type.Prinz => PrinzSupportPolicy.DynamicEntityId(RequiredPath(identity)),
            _ => StaticId.Hash(RequiredPath(identity))
        };

        private static string RequiredPath(string path) => string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A canonical native path is required.", nameof(path))
            : path;
    }
}
