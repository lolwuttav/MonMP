using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        private readonly Dictionary<ulong, VehiclePassengerSeatPosition> pendingPassengerSeatPositions =
            new Dictionary<ulong, VehiclePassengerSeatPosition>();

        // Restore the configuration belonging to the selected native checkpoint, before host records are registered.
        internal void RestoreConfiguration(IReadOnlyDictionary<string, SidecarSection> configuration)
        {
            if (!host) return;
            pendingPassengerSeatPositions.Clear();
            if (!configuration.TryGetValue(VehiclePassengerSeatPersistence.SectionKey, out var section)) return;
            try
            {
                foreach (var entry in VehiclePassengerSeatPersistence.Decode(section))
                    pendingPassengerSeatPositions.Add(entry.Key, entry.Value);
            }
            catch (InvalidDataException error)
            {
                Debug.LogWarning("MonMP skipped passenger-seat configuration: " + error.Message);
                return;
            }
            foreach (var adjustment in seatAdjustmentBindings.Values) RestorePassengerSeatPosition(adjustment);
        }

        internal Dictionary<string, SidecarSection> CaptureConfiguration(IReadOnlyDictionary<string, SidecarSection>? previousConfiguration)
        {
            var configuration = new Dictionary<string, SidecarSection>(System.StringComparer.Ordinal);
            if (previousConfiguration != null)
                foreach (var entry in previousConfiguration) configuration.Add(entry.Key, entry.Value);
            var positions = new Dictionary<ulong, VehiclePassengerSeatPosition>();
            int overflow = 0;
            foreach (var adjustment in seatAdjustmentBindings.Values.OrderBy(value => value.Id))
            {
                if (adjustment.Driver || !adjustment.RecordEnabled || adjustment.Vehicle.Dormant || adjustment.Seat == null) continue;
                var local = adjustment.Seat.transform.localPosition;
                if (!VehicleSeatAdjustmentRange.TryCentimeters(local.z, out var forward) ||
                    !VehicleSeatAdjustmentRange.TryCentimeters(local.y, out var height) ||
                    !PassengerSeatPositionInRange(adjustment, forward, height)) continue;
                if (VehicleSeatAdjustmentRange.TryCentimeters(adjustment.OriginalLocalPosition.z, out var defaultForward) &&
                    VehicleSeatAdjustmentRange.TryCentimeters(adjustment.OriginalLocalPosition.y, out var defaultHeight) &&
                    forward == defaultForward && height == defaultHeight) continue;
                if (positions.Count >= VehiclePassengerSeatPersistence.MaximumSeats) { overflow++; continue; }
                positions[adjustment.Id] = new VehiclePassengerSeatPosition(adjustment.Vehicle.Id, adjustment.Id,
                    forward, height);
            }
            // Deferred shells retain their selected checkpoint coordinates until native startup finishes.
            // Live known seats take priority over bounded, as-yet-unbound identities from older configurations.
            foreach (var entry in pendingPassengerSeatPositions.OrderBy(value => value.Key))
                if (!positions.ContainsKey(entry.Key))
                {
                    if (positions.Count < VehiclePassengerSeatPersistence.MaximumSeats) positions.Add(entry.Key, entry.Value);
                    else overflow++;
                }
            if (overflow != 0) Debug.LogWarning("MonMP passenger-seat configuration exceeded its bounded capacity; omitted " + overflow + " entries.");
            configuration[VehiclePassengerSeatPersistence.SectionKey] = VehiclePassengerSeatPersistence.Encode(positions.Values);
            return configuration;
        }

        private void RestorePassengerSeatPosition(SeatAdjustmentBinding adjustment)
        {
            if (!host || !pendingPassengerSeatPositions.TryGetValue(adjustment.Id, out var position)) return;
            if (adjustment.Driver || position.VehicleId != adjustment.Vehicle.Id)
            {
                pendingPassengerSeatPositions.Remove(adjustment.Id);
                Debug.LogWarning("MonMP skipped passenger-seat configuration with mismatched vehicle/seat identity.");
                return;
            }
            // Native Start initializes slider endpoints. Keep dormant/unbound shell coordinates separate from wire Pending.
            if (adjustment.Vehicle.Dormant || !adjustment.RecordEnabled || adjustment.Seat == null) return;
            pendingPassengerSeatPositions.Remove(adjustment.Id);
            if (!PassengerSeatPositionInRange(adjustment, position.ForwardCentimeters, position.HeightCentimeters))
            {
                Debug.LogWarning("MonMP skipped passenger-seat configuration outside the native seat range.");
                return;
            }
            ApplySeatAdjustment(adjustment, new VehicleSeatAdjustmentState(position.ForwardCentimeters,
                position.HeightCentimeters, adjustment.State.Generation));
        }

        private static bool PassengerSeatPositionInRange(SeatAdjustmentBinding adjustment, short forward, short height)
        {
            return System.Math.Abs((int)forward) <= VehiclePassengerSeatPersistence.MaximumPositionCentimeters &&
                System.Math.Abs((int)height) <= VehiclePassengerSeatPersistence.MaximumPositionCentimeters &&
                TrySeatAdjustmentLimits(adjustment.Seat, out var minForward, out var maxForward, out var minHeight, out var maxHeight) &&
                forward >= minForward && forward <= maxForward && height >= minHeight && height <= maxHeight;
        }
    }
}
