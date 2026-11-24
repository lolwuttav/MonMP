using MonMP.Core.Protocol;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedExtraSeats
    {
        // The right-hand cab seat, ATV riders, bus benches and bar stools keep their
        // existing interaction. Catalog/seat IDs remain present while disabled.
        private bool? appliedSeatSetting;
        private static bool OptionalTruckSeat(Seat seat) => seat.Truck != null && seat.Truck.vehicle_type == Vehicle_Type.OlTruck &&
            (seat.Kind == SeatKind.Ground || seat.Kind == SeatKind.Cab && seat.Slot == 0);
        private bool SeatEnabled(Seat seat) => session?.Settings.TruckExtraSeatsEnabled != false || !OptionalTruckSeat(seat);

        private void SetInteractionEnabled(Seat seat)
        {
            if (!OptionalTruckSeat(seat)) return;
            bool enabled = SeatEnabled(seat);
            foreach (var chair in seat.Chairs) if (chair != null) chair.enabled = enabled;
            if (enabled) return;
            foreach (var chair in seat.Chairs) SetPad(chair, false);
        }

        private void SeatSettingsChanged(SessionSettingsState state)
        {
            if (disposed || appliedSeatSetting == state.TruckExtraSeatsEnabled) return;
            appliedSeatSetting = state.TruckExtraSeatsEnabled;
            // Clear the cached target too, so a hover selected just before the toggle
            // cannot keep its glow or intercept the next native item interaction.
            lastPick = null;
            ShowHover(null);
            SeatAdmission?.CancelLocal(this);
            foreach (var seat in seats.Values) SetInteractionEnabled(seat);
            RevokeDisabledSeats();
        }

        private void RevokeDisabledSeats()
        {
            if (!Ready || session == null || session.Settings.TruckExtraSeatsEnabled) return;
            foreach (var seat in seats.Values)
            {
                if (SeatEnabled(seat)) continue;
                if (host && seat.State.Occupied && registry.TryRelease(seat.Key, seat.State.Owner, seat.State.Generation, out var released))
                {
                    Apply(seat, new VehiclePassengerSeatState(released.Peer, released.Generation));
                    Publish(seat);
                }
                // A setting can arrive before its occupancy delta, or during native
                // drinking. Exit immediately where possible; retain the existing retry.
                if (IsLocalSeated(seat)) seat.NativeExitPending = !TryLeaveNative(seat);
            }
        }
    }
}
