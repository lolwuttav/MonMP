using System.Linq;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedSchoolBusPassengers
    {
        internal bool HasLocalSaveSeat => Ready && session != null && !exitDeferred &&
            gameplay.Get_CurrentVehicleController != null &&
            gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && bindings.Values.Any(b =>
                b.Vehicle == gameplay.Get_CurrentVehicleController && b.State.Owner == session.LocalPeerId && !b.ExitPending);

        internal bool LocalSaveFlushComplete(bool hasOtherVehicleSeat) => LocalFlushComplete || HasLocalSaveSeat ||
            (session != null && !exitDeferred && hasOtherVehicleSeat && !Owners.Contains(session.LocalPeerId));

        internal void FlushForSave()
        {
            SeatAdmission?.CancelLocal(this);
            if (!HasLocalSaveSeat) FlushLocalOwnership();
        }
    }
}
