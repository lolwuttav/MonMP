using System;
using System.IO;

namespace MonMP.Core.World
{
    // A native local detector entry, never a client-created ticket. Vehicle speed,
    // heading, eligibility, spot activation and ticket accounting are host decisions.
    public readonly struct PoliceRadarRequest
    {
        public const byte Marker = 0xBF;
        public const int Size = 29;
        public readonly ulong Vehicle;
        public readonly uint Generation;
        public readonly float X, Y, Z, Drunkness;
        public bool HasDriver => Vehicle != 0;
        public PoliceRadarRequest(ulong vehicle, uint generation, float x, float y, float z, float drunkness)
        { Vehicle = vehicle; Generation = generation; X = x; Y = y; Z = z; Drunkness = drunkness; }
        private void Validate()
        {
            if ((Vehicle == 0) != (Generation == 0) || (!HasDriver && Drunkness != 0) || !ItemPose.Finite(X, 100000) || !ItemPose.Finite(Y, 100000) ||
                !ItemPose.Finite(Z, 100000) || !ItemPose.Finite(Drunkness, 1) || Drunkness < 0)
                throw new InvalidDataException("Invalid police radar report.");
        }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Vehicle); writer.Write(Generation);
            writer.Write(X); writer.Write(Y); writer.Write(Z); writer.Write(Drunkness);
            return stream.ToArray();
        }
        public static PoliceRadarRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Marker)
                throw new InvalidDataException("Invalid police radar report size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            var request = new PoliceRadarRequest(reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            request.Validate(); return request;
        }
    }

    public enum PoliceRadarDecision { None, Warning, Speeding }

    public static class PoliceRadarPolicy
    {
        // The exact PlayerDetected thresholds, including the native reverse-road
        // tolerance. The caller supplies a native-eligible vehicle and active spot.
        public static PoliceRadarDecision Decide(float speedKmh, float directionDot, int speedLimit, int reverseSpeedLimit, bool warningOnly)
        {
            if (!ItemPose.Finite(speedKmh, 1250) || speedKmh < 0 || !ItemPose.Finite(directionDot, 1.001f) ||
                speedLimit < 0 || reverseSpeedLimit < 0 || speedKmh <= speedLimit + 11f) return PoliceRadarDecision.None;
            if (!warningOnly && (directionDot > 0.1f || speedKmh > reverseSpeedLimit + 40f)) return PoliceRadarDecision.Speeding;
            return warningOnly || speedKmh > reverseSpeedLimit + 11f ? PoliceRadarDecision.Warning : PoliceRadarDecision.None;
        }
    }
}
