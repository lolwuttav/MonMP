using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum TireChangerAction : byte { MountWheel = 1, MountTire = 2, RunCycle = 3, RemoveWheel = 4 }
    public enum TireChangerOperation : byte { None = 0, DeRim = 1, Rim = 2 }

    // A machine is identified by its scene and sibling-indexed hierarchy path. IDs remain
    // static so the machine records are part of the normal synchronized world catalog.
    public static class TireChangerIdentity
    {
        public const ushort Adapter = 83;
        public static ulong Entity(string scenePath, string stableHierarchyPath)
        {
            if (string.IsNullOrWhiteSpace(scenePath) || string.IsNullOrWhiteSpace(stableHierarchyPath))
                throw new InvalidDataException("Tire-changer identity requires a scene and hierarchy path.");
            ulong id = StaticId.Hash("tire-changer/v1/" + scenePath + "/" + stableHierarchyPath) & ~WorldRecord.DynamicIdStart;
            return id == 0 ? 1 : id;
        }
    }

    public readonly struct TireChangerCommand
    {
        public const byte Marker = 0xD7;
        public const int Size = 31;
        public Guid OperationId { get; }
        public TireChangerAction Action { get; }
        public bool DeRimSide { get; }
        public ulong ItemEntity { get; }
        public uint Generation { get; }

        public TireChangerCommand(Guid operationId, TireChangerAction action, bool deRimSide,
            ulong itemEntity = 0, uint generation = 0)
        {
            OperationId = operationId; Action = action; DeRimSide = deRimSide;
            ItemEntity = itemEntity; Generation = generation;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(OperationId.ToByteArray()); writer.Write((byte)Action);
            writer.Write(DeRimSide); writer.Write(ItemEntity); writer.Write(Generation);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static TireChangerCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid tire-changer command size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var command = new TireChangerCommand(new Guid(reader.ReadBytes(16)), (TireChangerAction)reader.ReadByte(),
                reader.ReadBoolean(), reader.ReadUInt64(), reader.ReadUInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing tire-changer command data.");
            return command;
        }

        private void Validate()
        {
            if (OperationId == Guid.Empty || !Enum.IsDefined(typeof(TireChangerAction), Action))
                throw new InvalidDataException("Invalid tire-changer command identity or action.");
            bool itemAction = Action == TireChangerAction.MountWheel || Action == TireChangerAction.MountTire;
            bool removal = Action == TireChangerAction.RemoveWheel;
            if (itemAction != (ItemEntity > WorldRecord.DynamicIdStart && Generation != 0) ||
                (!itemAction && (ItemEntity != 0 || Generation != 0)) ||
                ((Action == TireChangerAction.MountTire || Action == TireChangerAction.RunCycle) && DeRimSide))
                throw new InvalidDataException("Invalid tire-changer command target.");
            if (Action == TireChangerAction.RunCycle && (DeRimSide || ItemEntity != 0 || Generation != 0))
                throw new InvalidDataException("Invalid tire-changer cycle command.");
            if (Action == TireChangerAction.MountTire && DeRimSide)
                throw new InvalidDataException("Tires can only be installed on the rim station.");
            if (removal && (ItemEntity != 0 || Generation != 0))
                throw new InvalidDataException("Wheel removal does not name a guest-controlled item.");
        }
    }

    // Slot membership and active cycle are presentation state. Item contents, ownership and
    // wheel/tire values remain in their ordinary ItemState records.
    public readonly struct TireChangerState : IEquatable<TireChangerState>
    {
        private const byte Marker = 0xD8;
        public ulong DeRimWheel { get; }
        public ulong RimWheel { get; }
        public ulong RimTire { get; }
        public uint OperationSequence { get; }
        public TireChangerOperation Operation { get; }
        public bool Working { get; }

        public TireChangerState(ulong deRimWheel, ulong rimWheel, ulong rimTire,
            uint operationSequence, TireChangerOperation operation, bool working)
        {
            DeRimWheel = deRimWheel; RimWheel = rimWheel; RimTire = rimTire;
            OperationSequence = operationSequence; Operation = operation; Working = working;
            Validate();
        }

        public static TireChangerState Initial => new TireChangerState(0, 0, 0, 0, TireChangerOperation.None, false);

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(31);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(DeRimWheel); writer.Write(RimWheel); writer.Write(RimTire);
            writer.Write(OperationSequence); writer.Write((byte)Operation); writer.Write(Working);
            return stream.ToArray();
        }

        public static TireChangerState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 31 || payload[0] != Marker)
                throw new InvalidDataException("Invalid tire-changer state size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var state = new TireChangerState(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
                reader.ReadUInt32(), (TireChangerOperation)reader.ReadByte(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing tire-changer state data.");
            return state;
        }

        public bool Equals(TireChangerState other) => DeRimWheel == other.DeRimWheel && RimWheel == other.RimWheel &&
            RimTire == other.RimTire && OperationSequence == other.OperationSequence && Operation == other.Operation && Working == other.Working;
        public override bool Equals(object? obj) => obj is TireChangerState other && Equals(other);
        public override int GetHashCode() => unchecked((((((DeRimWheel.GetHashCode() * 397) ^ RimWheel.GetHashCode()) * 397 ^
            RimTire.GetHashCode()) * 397 ^ (int)OperationSequence) * 397 ^ (int)Operation) * 397 ^ Working.GetHashCode());

        private void Validate()
        {
            static bool ValidItem(ulong entity) => entity == 0 || entity > WorldRecord.DynamicIdStart;
            if (!ValidItem(DeRimWheel) || !ValidItem(RimWheel) || !ValidItem(RimTire) ||
                (DeRimWheel != 0 && RimWheel != 0) || (RimTire != 0 && RimWheel == 0) ||
                !Enum.IsDefined(typeof(TireChangerOperation), Operation) ||
                (Working != (Operation != TireChangerOperation.None)) ||
                // Both native cycles let go of their wheel before the machine stops working (the de-rim
                // pops and drops it, the rim cycle frees it and returns the handle), so the tail of a
                // cycle legitimately has an empty slot (#201).
                (Operation == TireChangerOperation.DeRim && RimWheel != 0) ||
                (Operation == TireChangerOperation.Rim && (DeRimWheel != 0 || (RimWheel != 0) != (RimTire != 0))))
                throw new InvalidDataException("Invalid tire-changer slot or operation state.");
        }
    }
}
