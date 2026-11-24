using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct PoliceCollisionRequest
    {
        public const byte Marker = 0xBE;
        public const int Size = 45;
        public readonly Guid Operation;
        public readonly ulong Vehicle;
        public readonly uint Generation;
        public readonly float X, Y, Z, Force;
        public PoliceCollisionRequest(Guid operation, ulong vehicle, uint generation, float x, float y, float z, float force)
        {
            if (operation == Guid.Empty || vehicle == 0 || generation == 0 || !ItemPose.Finite(x, 100000) ||
                !ItemPose.Finite(y, 100000) || !ItemPose.Finite(z, 100000) || !ItemPose.Finite(force, 200) || force <= 8)
                throw new InvalidDataException("Invalid police collision report.");
            Operation = operation; Vehicle = vehicle; Generation = generation; X = x; Y = y; Z = z; Force = force;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Vehicle); writer.Write(Generation);
            writer.Write(X); writer.Write(Y); writer.Write(Z); writer.Write(Force); return stream.ToArray();
        }
        public static bool Is(byte[] data) => data != null && data.Length == Size && data[0] == Marker;
        public static PoliceCollisionRequest Decode(byte[] data)
        {
            if (!Is(data)) throw new InvalidDataException("Invalid police collision report size.");
            using var reader = new BinaryReader(new MemoryStream(data, false)); reader.ReadByte();
            return new PoliceCollisionRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
