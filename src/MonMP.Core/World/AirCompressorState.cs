using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host-owned compressor air pressure for the scene-mounted air tire pumps.
    public readonly struct AirCompressorState : IEquatable<AirCompressorState>
    {
        public const ushort Adapter = 63;
        private const byte Marker = 0xED;
        private const byte Version = 2;
        public readonly float Pressure;
        public readonly bool MotorRunning;

        public AirCompressorState(float pressure, bool motorRunning = false)
        {
            Pressure = pressure;
            MotorRunning = motorRunning;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(7);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write(Pressure);
            writer.Write(MotorRunning);
            return stream.ToArray();
        }

        public static AirCompressorState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 2 || payload[0] != Marker ||
                !(payload.Length == 6 && payload[1] == 1 || payload.Length == 7 && payload[1] == Version))
                throw new InvalidDataException("Invalid air compressor state.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadBytes(2);
            float pressure = reader.ReadSingle();
            if (payload.Length == 7 && payload[6] > 1) throw new InvalidDataException("Invalid compressor motor flag.");
            return new AirCompressorState(pressure, payload.Length == 7 && reader.ReadBoolean());
        }

        public bool Equals(AirCompressorState other) => Pressure.Equals(other.Pressure) && MotorRunning == other.MotorRunning;
        public override bool Equals(object? obj) => obj is AirCompressorState other && Equals(other);
        public override int GetHashCode() => Pressure.GetHashCode() ^ MotorRunning.GetHashCode();

        private void Validate()
        {
            if (float.IsNaN(Pressure) || float.IsInfinity(Pressure) || Pressure < 0 || Pressure > 100)
                throw new InvalidDataException("Invalid compressor air pressure.");
        }
    }
}
