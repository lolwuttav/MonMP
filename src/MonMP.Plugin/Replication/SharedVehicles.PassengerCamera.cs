using NWH.VehiclePhysics2;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        // Presentation reads an established local passenger claim. Native state
        // and attachment must agree; a stale lease alone cannot activate a view.
        internal VehicleController? CurrentLocalPassengerVehicle
        {
            get
            {
                if (!Ready || session == null || gameplay.Get_PlayerState != PlayerState.InVehicle_Passenger || gameplay.PlayerWalking == null) return null;
                foreach (var binding in passengerBindings.Values)
                    if (binding.Vehicle != null && !binding.Dormant && !binding.PassengerExitPending && binding.PassengerState.Occupied &&
                        binding.PassengerState.Owner == session.LocalPeerId && gameplay.Get_CurrentVehicleController == binding.Vehicle &&
                        gameplay.PlayerWalking.transform.parent == binding.Vehicle.transform) return binding.Vehicle;
                return null;
            }
        }
    }
}
