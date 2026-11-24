using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace MonMP.Core.Persistence
{
    /// <summary>World-owned seat coordinates. Occupants and session generations are never saved.</summary>
    public readonly struct VehiclePassengerSeatPosition
    {
        public readonly ulong VehicleId, SeatId;
        public readonly short ForwardCentimeters, HeightCentimeters;

        public VehiclePassengerSeatPosition(ulong vehicleId, ulong seatId, short forwardCentimeters, short heightCentimeters)
        {
            if (vehicleId == 0 || seatId == 0 || vehicleId == seatId ||
                Math.Abs((int)forwardCentimeters) > VehiclePassengerSeatPersistence.MaximumPositionCentimeters ||
                Math.Abs((int)heightCentimeters) > VehiclePassengerSeatPersistence.MaximumPositionCentimeters)
                throw new InvalidDataException("Invalid persistent passenger-seat identity or coordinates.");
            VehicleId = vehicleId; SeatId = seatId;
            ForwardCentimeters = forwardCentimeters; HeightCentimeters = heightCentimeters;
        }
    }

    public static class VehiclePassengerSeatPersistence
    {
        public const string SectionKey = "vehicle-passenger-seats";
        public const ushort SectionVersion = 1;
        public const int MaximumSeats = 512;
        // Coarse corruption bound; the owning native seat's exact slider limits are also required on restore.
        public const int MaximumPositionCentimeters = 1000;
        private const int EntryBytes = 20;

        public static SidecarSection Encode(IEnumerable<VehiclePassengerSeatPosition> positions)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            var entries = new Dictionary<ulong, VehiclePassengerSeatPosition>();
            foreach (var value in positions)
            {
                var entry = new VehiclePassengerSeatPosition(value.VehicleId, value.SeatId, value.ForwardCentimeters, value.HeightCentimeters);
                if (entries.ContainsKey(entry.SeatId) || entries.Count >= MaximumSeats)
                    throw new InvalidDataException("Duplicate or excessive persistent passenger seats.");
                entries.Add(entry.SeatId, entry);
            }
            using var stream = new MemoryStream(2 + entries.Count * EntryBytes);
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)entries.Count);
            foreach (var entry in entries.Values.OrderBy(value => value.SeatId))
            {
                writer.Write(entry.VehicleId); writer.Write(entry.SeatId);
                writer.Write(entry.ForwardCentimeters); writer.Write(entry.HeightCentimeters);
            }
            return new SidecarSection(SectionVersion, stream.ToArray());
        }

        public static IReadOnlyDictionary<ulong, VehiclePassengerSeatPosition> Decode(SidecarSection section)
        {
            if (section == null || section.Version != SectionVersion)
                throw new InvalidDataException("Unsupported persistent passenger-seat section version.");
            byte[] payload = section.Payload;
            if (payload.Length < 2) throw new InvalidDataException("Truncated persistent passenger-seat section.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            int count = reader.ReadUInt16();
            if (count > MaximumSeats || payload.Length != 2 + count * EntryBytes)
                throw new InvalidDataException("Invalid persistent passenger-seat section length or count.");
            var entries = new Dictionary<ulong, VehiclePassengerSeatPosition>();
            for (int index = 0; index < count; index++)
            {
                var entry = new VehiclePassengerSeatPosition(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt16(), reader.ReadInt16());
                if (entries.ContainsKey(entry.SeatId)) throw new InvalidDataException("Duplicate persistent passenger-seat identity.");
                entries.Add(entry.SeatId, entry);
            }
            return new ReadOnlyDictionary<ulong, VehiclePassengerSeatPosition>(entries);
        }
    }
}
