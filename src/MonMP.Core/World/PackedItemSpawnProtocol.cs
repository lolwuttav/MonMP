using System;
using System.IO;

namespace MonMP.Core.World
{
    // One native extraction, one exact predicted output, and one authoritative pack debit.
    // Values come exclusively from the host's ObjectToSpawn + EditSpawnedObject.
    public readonly struct PackedItemSpawnRequest
    {
        public const byte Marker = 0xA6;
        public const int Size = 68;
        public readonly uint Nonce, Generation;
        public readonly ushort Prefab;
        public readonly bool Hold;
        public readonly ItemPose PackPose, OutputPose;
        public PackedItemSpawnRequest(uint nonce, uint generation, ushort prefab, bool hold, ItemPose packPose, ItemPose outputPose)
        { Nonce = nonce; Generation = generation; Prefab = prefab; Hold = hold; PackPose = packPose; OutputPose = outputPose; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Nonce); writer.Write(Generation); writer.Write(Prefab); writer.Write(Hold);
            PackPose.Write(writer); OutputPose.Write(writer); return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static PackedItemSpawnRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid packed output request.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); uint nonce = reader.ReadUInt32(), generation = reader.ReadUInt32(); ushort prefab = reader.ReadUInt16(); byte hold = reader.ReadByte();
            var request = new PackedItemSpawnRequest(nonce, generation, prefab, hold == 1, ItemPose.Read(reader), ItemPose.Read(reader));
            if (hold > 1) throw new InvalidDataException("Invalid packed output hold flag.");
            request.Validate(); return request;
        }
        private void Validate()
        {
            if (Nonce == 0 || Generation == 0 || Prefab == 0) throw new InvalidDataException("Invalid packed output identity.");
            PackPose.Validate(); OutputPose.Validate();
        }
    }
    public enum PackedItemSpawnStatus : byte { Accepted = 1, Rejected = 2, Retry = 3 }
    public readonly struct PackedItemSpawnReply
    {
        public const byte Marker = 0xA7;
        public const int Size = 14;
        public readonly uint Nonce;
        public readonly PackedItemSpawnStatus Status;
        public readonly ulong Entity;
        public PackedItemSpawnReply(uint nonce, PackedItemSpawnStatus status, ulong entity = 0)
        { Nonce = nonce; Status = status; Entity = entity; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Nonce); writer.Write((byte)Status); writer.Write(Entity); return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static PackedItemSpawnReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid packed output reply.");
            var reply = new PackedItemSpawnReply(BitConverter.ToUInt32(payload, 1), (PackedItemSpawnStatus)payload[5], BitConverter.ToUInt64(payload, 6));
            reply.Validate(); return reply;
        }
        private void Validate()
        {
            if (Nonce == 0 || !Enum.IsDefined(typeof(PackedItemSpawnStatus), Status) ||
                (Status == PackedItemSpawnStatus.Accepted ? Entity <= WorldRecord.DynamicIdStart : Entity != 0))
                throw new InvalidDataException("Invalid packed output receipt.");
        }
    }
}
