using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    public enum ItemPickupAction : byte { Acquire = 1, Cancel = 2 }
    public enum ItemPickupOutcome : byte { Granted = 1, Refused = 2, Cancelled = 3, Unclaimed = 4 }

    // No client position or value authority: acquisition uses the host's live item
    // and the same native/range/lease checks as an ordinary ItemAction.Acquire.
    public readonly struct ItemPickupRequest
    {
        public const byte Marker = 0xE5;
        public const int Size = 10;
        public readonly uint Nonce, Generation;
        public readonly ItemPickupAction Action;
        public ItemPickupRequest(uint nonce, uint generation, ItemPickupAction action)
        { Nonce = nonce; Generation = generation; Action = action; }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            Validate(); var bytes = new byte[Size]; bytes[0] = Marker; bytes[1] = (byte)Action;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), Nonce);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), Generation); return bytes;
        }
        public static ItemPickupRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid item pickup request.");
            var value = new ItemPickupRequest(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6, 4)), (ItemPickupAction)payload[1]);
            value.Validate(); return value;
        }
        private void Validate()
        {
            if (Nonce == 0 || Generation == 0 || (Action != ItemPickupAction.Acquire && Action != ItemPickupAction.Cancel))
                throw new InvalidDataException("Invalid item pickup request identity/action.");
        }
        public static uint GrantedGeneration(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;
    }

    public readonly struct ItemPickupReply
    {
        private const byte Marker = 0xE5;
        public const int Size = 10;
        public readonly uint Nonce, Generation;
        public readonly ItemPickupOutcome Outcome;
        public ItemPickupReply(uint nonce, uint generation, ItemPickupOutcome outcome)
        { Nonce = nonce; Generation = generation; Outcome = outcome; }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            Validate(); var bytes = new byte[Size]; bytes[0] = Marker; bytes[1] = (byte)Outcome;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), Nonce);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), Generation); return bytes;
        }
        public static ItemPickupReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid item pickup reply.");
            var value = new ItemPickupReply(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6, 4)), (ItemPickupOutcome)payload[1]);
            value.Validate(); return value;
        }
        private void Validate()
        {
            if (Nonce == 0 || Generation == 0 || Outcome < ItemPickupOutcome.Granted || Outcome > ItemPickupOutcome.Unclaimed)
                throw new InvalidDataException("Invalid item pickup reply identity/outcome.");
        }
    }
}
