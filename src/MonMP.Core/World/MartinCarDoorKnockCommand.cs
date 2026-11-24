using System;
using System.IO;

namespace MonMP.Core.World
{
    // Guest requests to knock at Martin's home door. The entity id is carried by
    // WorldChannel; a non-zero session-local nonce prevents replay on that route.
    public readonly struct MartinCarDoorKnockCommand
    {
        public const byte Marker0 = 0x4D;
        public const byte Marker1 = 0x43;
        public const byte Action = 2;
        public const int Size = 7;
        public uint Nonce { get; }

        public MartinCarDoorKnockCommand(uint nonce)
        {
            if (nonce == 0) throw new InvalidDataException("Invalid Martin car door-knock nonce.");
            Nonce = nonce;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker0); writer.Write(Marker1); writer.Write(Action); writer.Write(Nonce);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size &&
            payload[0] == Marker0 && payload[1] == Marker1 && payload[2] == Action;

        public static MartinCarDoorKnockCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid Martin car door-knock command.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            _ = reader.ReadByte(); _ = reader.ReadByte(); _ = reader.ReadByte();
            return new MartinCarDoorKnockCommand(reader.ReadUInt32());
        }
    }
}
