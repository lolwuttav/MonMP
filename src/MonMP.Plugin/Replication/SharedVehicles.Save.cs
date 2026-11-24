using System.Linq;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        // Seat leases are session-only. A checkpoint need not dismantle an established
        // native seat; pending entry/exit and ordinary disconnects still drain normally.
        internal bool HasLocalSaveSeat
        {
            get
            {
                if (!Ready || session == null || exitDeferred) return false;
                var current = gameplay.Get_CurrentVehicleController;
                if (current == null) return false;
                return bindings.Values.Any(b => b.Vehicle == current && !b.Dormant &&
                    (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver &&
                        b.State.Driver.Owner == session.LocalPeerId && !b.ExitPending ||
                     gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && b.PassengerId != 0 &&
                        b.PassengerState.Owner == session.LocalPeerId && !b.PassengerExitPending));
            }
        }

        internal bool LocalSaveFlushComplete(bool hasOtherVehicleSeat) => LocalFlushComplete || HasLocalSaveSeat ||
            (session != null && !exitDeferred && hasOtherVehicleSeat && !Owners.Contains(session.LocalPeerId));

        internal void FlushForSave(bool hasOtherVehicleSeat)
        {
            SeatAdmission?.CancelLocal(this);
            if (HasLocalSaveSeat || hasOtherVehicleSeat && session != null && !exitDeferred &&
                !Owners.Contains(session.LocalPeerId)) return;
            FlushLocalOwnership();
        }
    }
}
