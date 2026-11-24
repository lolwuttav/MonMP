using System;
using System.IO;

namespace MonMP.Core.World
{
    // The bin record routes private deposit-feedback requests. Actual deletion follows the
    // native Wednesday EmptyTrash event; no client packet is allowed to delete an item.
    public static class TrashRecycleProtocol
    {
        public const ushort Adapter = 42;
        public const byte Marker = 0xDA;
        public static byte[] Record() => new byte[] { 1 };
        public static void ValidateRecord(byte[] payload)
        {
            if (payload == null || payload.Length != 1 || payload[0] != 1)
                throw new InvalidDataException("Invalid trash/recycle bin record.");
        }
        // Matches TrashRecycle.EmptyTrash's native predicate, including the combined bin mode.
        public static bool Eligible(bool isRecycle, bool isBoth, bool canTrash, bool canRecycle) =>
            (isBoth && (canTrash || canRecycle)) || (canTrash && !isRecycle) || (canRecycle && isRecycle);

        // All inputs to a host decision must come from the host's scene, item lease and
        // recent walking pose. A client supplies only the bin, item and operation IDs.
        public static TrashRecycleStatus Decide(bool active, bool inRange, bool itemExists,
            bool ownerAllowed, bool released, bool eligible, bool inside)
        {
            if (!active) return TrashRecycleStatus.NotInBin;
            if (!inRange) return TrashRecycleStatus.TooFar;
            if (!itemExists) return TrashRecycleStatus.MissingItem;
            if (!ownerAllowed || !released) return TrashRecycleStatus.NotLoose;
            if (!eligible) return TrashRecycleStatus.Ineligible;
            return inside ? TrashRecycleStatus.Accepted : TrashRecycleStatus.NotInBin;
        }
    }

    public enum TrashRecycleStatus : byte
    {
        Accepted = 1, TooFar, Ineligible, MissingItem, NotInBin, NotLoose, Blocked, Capacity, RetryRejected
    }

    public readonly struct TrashRecycleRequest
    {
        public const int Size = 25;
        public readonly Guid Operation;
        public readonly ulong Item;
        public TrashRecycleRequest(Guid operation, ulong item)
        {
            if (operation == Guid.Empty || item <= WorldRecord.DynamicIdStart)
                throw new InvalidDataException("Invalid trash/recycle request.");
            Operation = operation; Item = item;
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == TrashRecycleProtocol.Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(TrashRecycleProtocol.Marker); writer.Write(Operation.ToByteArray()); writer.Write(Item);
            return stream.ToArray();
        }
        public static TrashRecycleRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid trash/recycle request size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
            return new TrashRecycleRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64());
        }
    }

    public readonly struct TrashRecycleReply
    {
        public const int Size = 26;
        public readonly Guid Operation;
        public readonly ulong Item;
        public readonly TrashRecycleStatus Status;
        public TrashRecycleReply(Guid operation, ulong item, TrashRecycleStatus status)
        {
            if (operation == Guid.Empty || item <= WorldRecord.DynamicIdStart ||
                !Enum.IsDefined(typeof(TrashRecycleStatus), status))
                throw new InvalidDataException("Invalid trash/recycle reply.");
            Operation = operation; Item = item; Status = status;
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == TrashRecycleProtocol.Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(TrashRecycleProtocol.Marker); writer.Write(Operation.ToByteArray()); writer.Write(Item); writer.Write((byte)Status);
            return stream.ToArray();
        }
        public static TrashRecycleReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid trash/recycle reply size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
            return new TrashRecycleReply(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), (TrashRecycleStatus)reader.ReadByte());
        }
    }
}
