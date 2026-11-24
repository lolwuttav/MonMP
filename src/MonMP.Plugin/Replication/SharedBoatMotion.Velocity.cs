using NWH;
using NWH.DWP2.ShipController;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedBoatMotion
    {
        internal bool TryPresentedVelocity(Vehicle vehicle, out Vector3 velocity)
        {
            velocity = default;
            if (!Ready || host || vehicle is not AdvancedShipController ship || !byShip.TryGetValue(ship, out var binding) ||
                !binding.HasPose || Time.unscaledTime - binding.PoseReceivedAt > 1f)
                return false;
            // Both boats stay host-simulated, including the local guest driver's
            // kinematic body. Input ownership does not make its native speed valid.
            velocity = binding.TargetVelocity;
            return true;
        }
    }
}
