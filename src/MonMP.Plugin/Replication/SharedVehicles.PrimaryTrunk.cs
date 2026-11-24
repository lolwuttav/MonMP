using NWH.VehiclePhysics2;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        // The Buggy's usable hood is a Vehicle_Trunk without a parts-manager reference.
        // Keep discovery and body admission on the same exact native lid, open or closed.
        internal static Vehicle_Trunk? PrimaryTrunk(VehicleController vehicle) =>
            vehicle.GetComponent<Vehicle_PartsManager>()?.Get_Vehicle_Trunk ??
            (vehicle.vehicle_type == Vehicle_Type.Buggy
                ? vehicle.transform.Find("Buggy/TrunkHood")?.GetComponent<Vehicle_Trunk>() : null);
    }
}
