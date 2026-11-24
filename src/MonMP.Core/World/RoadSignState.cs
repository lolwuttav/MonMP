using System;
using System.IO;

namespace MonMP.Core.World
{
    // BendOnCollision signs are scene objects, with a single session-only impact.
    public readonly struct RoadSignState
    {
        public const ushort Adapter = 155;
        public const int Size = 14;
        public readonly bool Bent, ColliderEnabled;
        public readonly float X, Y, Z;

        public RoadSignState(bool bent, bool colliderEnabled, float x, float y, float z)
        { Bent = bent; ColliderEnabled = colliderEnabled; X = x; Y = y; Z = z; }

        private void Validate()
        {
            if (!Angle(X) || !Angle(Y) || !Angle(Z)) throw new InvalidDataException("Invalid road sign rotation.");
        }
        private static bool Angle(float value) => ItemPose.Finite(value, 360f) && value >= 0f;
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Bent); writer.Write(ColliderEnabled); writer.Write(X); writer.Write(Y); writer.Write(Z);
            return stream.ToArray();
        }
        public static RoadSignState Decode(byte[] data)
        {
            if (data == null || data.Length != Size || data[0] > 1 || data[1] > 1)
                throw new InvalidDataException("Invalid road sign state payload.");
            var state = new RoadSignState(data[0] == 1, data[1] == 1,
                BitConverter.ToSingle(data, 2), BitConverter.ToSingle(data, 6), BitConverter.ToSingle(data, 10));
            state.Validate(); return state;
        }
    }

    // The guest reports a contact, never a sign rotation or a collider mutation.
    public readonly struct RoadSignImpactCommand
    {
        public const int Size = 29;
        private const byte Marker = 1;
        public readonly ulong Vehicle;
        public readonly uint Generation;
        public readonly float Speed, X, Y, Z;

        public RoadSignImpactCommand(ulong vehicle, uint generation, float speed, float x, float y, float z)
        { Vehicle = vehicle; Generation = generation; Speed = speed; X = x; Y = y; Z = z; }
        private void Validate()
        {
            if (Vehicle == 0 || Generation == 0 || !ItemPose.Finite(Speed, 12f) || Speed <= 1f ||
                !ItemPose.Finite(X, 100000f) || !ItemPose.Finite(Y, 100000f) || !ItemPose.Finite(Z, 100000f))
                throw new InvalidDataException("Invalid road sign impact command.");
        }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Vehicle); writer.Write(Generation); writer.Write(Speed);
            writer.Write(X); writer.Write(Y); writer.Write(Z); return stream.ToArray();
        }
        public static RoadSignImpactCommand Decode(byte[] data)
        {
            if (data == null || data.Length != Size || data[0] != Marker)
                throw new InvalidDataException("Invalid road sign impact payload.");
            var command = new RoadSignImpactCommand(BitConverter.ToUInt64(data, 1), BitConverter.ToUInt32(data, 9),
                BitConverter.ToSingle(data, 13), BitConverter.ToSingle(data, 17), BitConverter.ToSingle(data, 21), BitConverter.ToSingle(data, 25));
            command.Validate(); return command;
        }
    }

    public static class RoadSignImpactPolicy
    {
        public static bool Accepts(float impactSpeed, float acceptedSpeed, float sampleAge,
            float surfaceDistanceSquared, float vehicleDistanceSquared)
        {
            if (!ItemPose.Finite(impactSpeed, 12f) || impactSpeed <= 1f ||
                !ItemPose.Finite(acceptedSpeed, 350f) || acceptedSpeed < 0f ||
                !ItemPose.Finite(sampleAge, 1f) || sampleAge < 0f ||
                !ItemPose.Finite(surfaceDistanceSquared, 1f) || surfaceDistanceSquared < 0f ||
                !ItemPose.Finite(vehicleDistanceSquared, 400f) || vehicleDistanceSquared < 0f) return false;
            // Samples and contacts can straddle the physics step. Allow one short
            // acceleration interval, then cap travel and the native bend speed.
            float reach = 8f + Math.Min(12f, acceptedSpeed * Math.Min(sampleAge + 0.2f, 0.5f));
            return impactSpeed <= acceptedSpeed + 4f && surfaceDistanceSquared <= 0.75f * 0.75f &&
                vehicleDistanceSquared <= reach * reach;
        }
    }
}
