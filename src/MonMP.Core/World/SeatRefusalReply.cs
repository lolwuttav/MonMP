using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    // The host's answer to a seat claim it turned down, so the requester's pending claim
    // ends at once instead of at its timeout. Its length and tag differ from every other
    // reply sent for seat records (passenger state 8 bytes, Martin 16, furniture 27).
    public readonly struct SeatRefusalReply
    {
        public const int Size = 6;
        private const byte Tag = 0xE3;
        public readonly FurnitureSeatRefusal Reason;
        public readonly uint Generation;

        public SeatRefusalReply(FurnitureSeatRefusal reason, uint generation)
        {
            if (reason == FurnitureSeatRefusal.None || !Enum.IsDefined(typeof(FurnitureSeatRefusal), reason))
                throw new ArgumentException("Invalid seat refusal.");
            Reason = reason; Generation = generation;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Tag;

        public byte[] Encode()
        {
            var bytes = new byte[Size];
            bytes[0] = Tag; bytes[1] = (byte)Reason;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), Generation);
            return bytes;
        }

        public static SeatRefusalReply Decode(byte[] payload)
        {
            if (!Is(payload) || payload[1] == (byte)FurnitureSeatRefusal.None || !Enum.IsDefined(typeof(FurnitureSeatRefusal), payload[1]))
                throw new InvalidDataException("Invalid seat refusal.");
            return new SeatRefusalReply((FurnitureSeatRefusal)payload[1], BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2)));
        }
    }
}
