using System;
using System.IO;

namespace MonMP.Core.World
{
    // Literal presentation only; the host retains the native bottle refill and wallet debit.
    public readonly struct NitrousRefillStationState : IEquatable<NitrousRefillStationState>
    {
        public const ushort Adapter = 137;
        public const int Size = 3;
        public readonly bool Filling;
        public readonly ushort Pressure;
        public float PressureValue => Pressure / 65535f;

        public NitrousRefillStationState(bool filling, float pressure)
        {
            if (float.IsNaN(pressure) || float.IsInfinity(pressure) || pressure < 0 || pressure > 1)
                throw new InvalidDataException("Invalid nitrous station pressure.");
            Filling = filling;
            Pressure = (ushort)Math.Round(pressure * 65535f);
        }

        public byte[] Encode() => new[] { (byte)(Filling ? 1 : 0), (byte)Pressure, (byte)(Pressure >> 8) };

        public static NitrousRefillStationState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] > 1)
                throw new InvalidDataException("Invalid nitrous station presentation payload.");
            return new NitrousRefillStationState(payload[0] == 1, (payload[1] | payload[2] << 8) / 65535f);
        }

        public bool Equals(NitrousRefillStationState other) => Filling == other.Filling && Pressure == other.Pressure;
        public override bool Equals(object? obj) => obj is NitrousRefillStationState other && Equals(other);
        public override int GetHashCode() => (Filling ? 65536 : 0) | Pressure;
    }
}
