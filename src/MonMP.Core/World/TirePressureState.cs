using System;
using System.IO;

namespace MonMP.Core.World
{
    // Pressure and mounted-tire identity for a socket on the catalogued shared fleet.
    // TireType is zero only when the native wheel is rim-only.
    public readonly struct TirePressureState : IEquatable<TirePressureState>
    {
        public const ushort Adapter = 59;
        private const byte Marker = 0xEB;
        private const byte Version = 1;
        public readonly byte TireType;
        public readonly float Pressure;
        public bool HasTire => TireType != 0;

        public TirePressureState(byte tireType, float pressure)
        {
            TireType = tireType;
            Pressure = pressure;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(7);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write(TireType); writer.Write(Pressure);
            return stream.ToArray();
        }

        public static TirePressureState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 7 || payload[0] != Marker || payload[1] != Version)
                throw new InvalidDataException("Invalid tire pressure state.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadBytes(3);
            return new TirePressureState(payload[2], reader.ReadSingle());
        }

        public bool Equals(TirePressureState other) => TireType == other.TireType && Pressure.Equals(other.Pressure);
        public override bool Equals(object? obj) => obj is TirePressureState other && Equals(other);
        public override int GetHashCode() => (TireType.GetHashCode() * 397) ^ Pressure.GetHashCode();

        private void Validate()
        {
            if (TireType > 6 || !Finite(Pressure) || Pressure < -100000 || Pressure > 100 || (!HasTire && Pressure != 0))
                throw new InvalidDataException("Invalid native tire type or pressure.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
