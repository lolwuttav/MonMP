using System;
using System.IO;

namespace MonMP.Core.World
{
    // The game's WindowTintData persists only tinted windows. Keeping the untinted
    // material color too makes snapshots and disconnect restoration deterministic.
    public readonly struct WindowTintState
    {
        public const ushort Adapter = 41;
        private const byte Marker = 0xDB;
        private const byte Version = 1;
        public readonly bool Tinted;
        public readonly float Red, Green, Blue, Alpha;

        public WindowTintState(bool tinted, float red, float green, float blue, float alpha)
        {
            Tinted = tinted; Red = red; Green = green; Blue = blue; Alpha = alpha;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(19);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write(Tinted);
            writer.Write(Red); writer.Write(Green); writer.Write(Blue); writer.Write(Alpha);
            return stream.ToArray();
        }

        // Match the serialized floats exactly, including the sign of zero, without
        // constructing two streams and payloads for every unchanged polling sample.
        public bool SameValue(WindowTintState other) => Tinted == other.Tinted &&
            BitConverter.SingleToInt32Bits(Red) == BitConverter.SingleToInt32Bits(other.Red) &&
            BitConverter.SingleToInt32Bits(Green) == BitConverter.SingleToInt32Bits(other.Green) &&
            BitConverter.SingleToInt32Bits(Blue) == BitConverter.SingleToInt32Bits(other.Blue) &&
            BitConverter.SingleToInt32Bits(Alpha) == BitConverter.SingleToInt32Bits(other.Alpha);

        public static WindowTintState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 19 || payload[0] != Marker || payload[1] != Version || payload[2] > 1)
                throw new InvalidDataException("Invalid window tint state.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadBytes(3);
            return new WindowTintState(payload[2] == 1, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private void Validate()
        {
            if (!Component(Red) || !Component(Green) || !Component(Blue) || !Component(Alpha))
                throw new InvalidDataException("Invalid window tint color.");
        }
        private static bool Component(float value) => ItemPose.FiniteValue(value, 16f) && value >= 0;
    }

    public readonly struct WindowTintCommand
    {
        private const byte Marker = 0xDC;
        public readonly Guid Operation;
        public readonly bool Remove;
        public readonly ulong ToolId;
        public readonly uint ToolGeneration;
        public WindowTintCommand(Guid operation, bool remove, ulong toolId, uint toolGeneration)
        {
            if (operation == Guid.Empty || toolId == 0 || toolGeneration == 0)
                throw new InvalidDataException("Invalid window tint command.");
            Operation = operation; Remove = remove; ToolId = toolId; ToolGeneration = toolGeneration;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(30);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Remove);
            writer.Write(ToolId); writer.Write(ToolGeneration);
            return stream.ToArray();
        }
        public static WindowTintCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 30 || payload[0] != Marker || payload[17] > 1)
                throw new InvalidDataException("Invalid window tint command.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            bool remove = reader.ReadBoolean();
            return new WindowTintCommand(operation, remove, reader.ReadUInt64(), reader.ReadUInt32());
        }
    }
}
