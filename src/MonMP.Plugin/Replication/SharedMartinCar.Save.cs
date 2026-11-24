using System.Linq;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedMartinCar
    {
        internal bool HasLocalSaveSeat => PassengerReady && session != null && bindings.Values.Any(b =>
            b.Vehicle != null && b.Passenger.Owner == session.LocalPeerId && !b.EnterPending && !b.CancelEntry && !b.ExitPending && IsNativePassenger(b));

        internal bool LocalSaveFlushComplete => LocalFlushComplete || HasLocalSaveSeat;

        internal void FlushForSave()
        {
            SeatAdmission?.CancelLocal(this);
            if (!HasLocalSaveSeat) FlushLocalOwnership();
        }
    }
}
