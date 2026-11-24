using System.Linq;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedExtraSeats
    {
        // Preserve installed vehicle seats, including the truck/ATV/bus additions.
        // A loose stool remains part of the ordinary ownership settlement.
        internal uint[] SaveOwners => seats.Values.Where(seat => seat.Truck == null)
            .Select(seat => seat.State.Owner).Where(owner => owner != SeatOccupancy.Unoccupied).Distinct().ToArray();

        private bool KeepForSave(Seat seat) => session != null && seat.State.Owner == session.LocalPeerId && seat.Truck != null && seat.Placed &&
            !seat.ExitPending && !seat.NativeExitPending && IsLocalSeated(seat);

        internal bool HasLocalSaveSeat => session != null && seats.Values.Any(seat =>
            seat.State.Owner == session.LocalPeerId && KeepForSave(seat));

        internal bool LocalSaveFlushComplete => session == null ||
            !seats.Values.Any(seat => seat.NativeExitPending ||
                (seat.State.Owner == session.LocalPeerId || IsLocalSeated(seat)) && !KeepForSave(seat));

        internal void FlushForSave()
        {
            SeatAdmission?.CancelLocal(this);
            if (!Ready || session == null) return;
            foreach (var seat in seats.Values.Where(seat => seat.State.Owner == session.LocalPeerId && !KeepForSave(seat)).ToArray())
                StandUp(seat);
        }

        internal void RevokeForSave(uint peer)
        {
            if (!host) return;
            foreach (var seat in seats.Values.Where(seat => seat.Truck == null && seat.State.Owner == peer).ToArray())
            {
                if (!registry.TryRelease(seat.Key, peer, seat.State.Generation, out var released)) continue;
                Apply(seat, new VehiclePassengerSeatState(released.Peer, released.Generation));
                Publish(seat);
            }
        }
    }
}
