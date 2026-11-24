using System;
using MonMP.Core.World;
using NWH.VehiclePhysics2;

namespace MonMP.Plugin.Replication
{
    // Dynamic fuel controls on supported sale clones keep their identity when a cap reparents
    // to its authored open location. The caller supplies a vehicle-root-relative stable anchor.
    internal static class PurchasedVehicleFuelIdentityPolicy
    {
        internal static bool Supports(Vehicle_Type type) =>
            type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV;

        internal static string Identity(ulong saleEntity, Vehicle_Type type, string relativePath, FuelKind kind)
        {
            if (saleEntity <= WorldRecord.DynamicIdStart)
                throw new ArgumentOutOfRangeException(nameof(saleEntity), "A purchased vehicle sale entity must be dynamic.");
            if (!Supports(type)) throw new ArgumentOutOfRangeException(nameof(type), "Fuel controls are not enabled for this sale vehicle type.");
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("A vehicle-root-relative fuel path is required.", nameof(relativePath));
            string role = kind switch
            {
                FuelKind.Cap => "fuel-cap-v1",
                FuelKind.Door => "fuel-door-v1",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only fuel caps and doors use purchased fuel identities.")
            };
            return "monmp/purchased-vehicle-fuel/v1/" + saleEntity.ToString("X16") + "/" + type + "/" + relativePath + "/" + role;
        }

        internal static ulong EntityId(ulong saleEntity, Vehicle_Type type, string relativePath, FuelKind kind) =>
            PrinzSupportPolicy.DynamicEntityId(Identity(saleEntity, type, relativePath, kind));
    }
}
