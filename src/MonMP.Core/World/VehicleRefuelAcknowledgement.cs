using System;
using System.IO;

namespace MonMP.Core.World
{
    // Sent on the same reliable ordered command stream before any driver frame that
    // includes this credit. No fuel is granted by an acknowledgement.
    public readonly struct VehicleRefuelAcknowledgement
    {
        public const byte Marker = 0xF4;
        public readonly uint Generation;
        public readonly double Credit;
        public VehicleRefuelAcknowledgement(uint generation, double credit)
        {
            if (generation == 0 || !ValidCredit(credit)) throw new InvalidDataException("Invalid vehicle refuel acknowledgement.");
            Generation = generation; Credit = credit;
        }
        public static bool ValidCredit(double credit) => !double.IsNaN(credit) && !double.IsInfinity(credit) && credit >= 0 && credit <= 1e9;
        public static bool Is(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Generation); writer.Write(Credit); return stream.ToArray();
        }
        public static VehicleRefuelAcknowledgement Decode(byte[] payload)
        {
            if (!Is(payload) || payload.Length != 13) throw new InvalidDataException("Invalid vehicle refuel acknowledgement.");
            return new VehicleRefuelAcknowledgement(BitConverter.ToUInt32(payload, 1), BitConverter.ToDouble(payload, 5));
        }
    }
}
