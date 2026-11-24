using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum BoltMountAction : byte { Install = 1, Remove = 2 }

    // The world command envelope supplies the exact hole and its expected revision.
    // Installation names a loose-item lease; no client-selected tightening value is accepted.
    public readonly struct BoltMountCommand
    {
        public const byte Marker = 0xBA;
        public const int Size = 30;
        public readonly Guid Operation;
        public readonly BoltMountAction Action;
        public readonly ulong Item;
        public readonly uint Generation;
        public BoltMountCommand(Guid operation, BoltMountAction action, ulong item = 0, uint generation = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(BoltMountAction), action) ||
                (action == BoltMountAction.Install ? item <= WorldRecord.DynamicIdStart || generation == 0 : item != 0 || generation != 0))
                throw new InvalidDataException("Invalid bolt mount command.");
            Operation = operation; Action = action; Item = item; Generation = generation;
        }
        public byte[] Encode()
        {
            _ = new BoltMountCommand(Operation, Action, Item, Generation);
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Action); writer.Write(Operation.ToByteArray()); writer.Write(Item); writer.Write(Generation);
            return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static BoltMountCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid bolt mount payload.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte(); var action = (BoltMountAction)reader.ReadByte();
            return new BoltMountCommand(new Guid(reader.ReadBytes(16)), action, reader.ReadUInt64(), reader.ReadUInt32());
        }
    }
}
