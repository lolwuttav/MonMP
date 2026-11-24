using System;
using System.IO;

namespace MonMP.Core.World
{
    // Structural socket changes carry the exact installed source and loose-item lease.
    // A reply identifies the actor whose native output is allowed to become a loose item.
    public readonly struct VehiclePartChangeCommand : IEquatable<VehiclePartChangeCommand>
    {
        public const byte Marker = 0xDE;
        public readonly Guid Operation;
        public readonly VehiclePartState Before, After;
        public readonly ulong Item;
        public readonly uint Generation;
        public readonly Guid ParentOperation;
        public readonly ulong ParentSocket;
        public VehiclePartChangeCommand(Guid operation, VehiclePartState before, VehiclePartState after, ulong item = 0, uint generation = 0, Guid parentOperation = default, ulong parentSocket = 0)
        {
            if (operation == Guid.Empty || before.Installed == after.Installed ||
                (after.Installed ? item <= WorldRecord.DynamicIdStart || generation == 0 : item != 0 || generation != 0) ||
                (parentOperation == Guid.Empty ? parentSocket != 0 : after.Installed || parentSocket == 0 || parentOperation == operation))
                throw new InvalidDataException("Invalid structural vehicle part command.");
            Operation = operation; Before = before; After = after; Item = item; Generation = generation;
            ParentOperation = parentOperation; ParentSocket = parentSocket;
        }
        public static bool Is(byte[] payload) => payload.Length > 0 && payload[0] == Marker;
        public byte[] Encode()
        {
            _ = new VehiclePartChangeCommand(Operation, Before, After, Item, Generation, ParentOperation, ParentSocket);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Before.Encode()); writer.Write(After.Encode()); writer.Write(Item); writer.Write(Generation);
            writer.Write(ParentOperation.ToByteArray()); writer.Write(ParentSocket);
            return stream.ToArray();
        }
        public static VehiclePartChangeCommand Decode(byte[] payload)
        {
            if (payload.Length != 91 || !Is(payload)) throw new InvalidDataException("Invalid structural vehicle part payload.");
            using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
            return new VehiclePartChangeCommand(new Guid(reader.ReadBytes(16)), VehiclePartState.Decode(reader.ReadBytes(19)), VehiclePartState.Decode(reader.ReadBytes(19)), reader.ReadUInt64(), reader.ReadUInt32(), new Guid(reader.ReadBytes(16)), reader.ReadUInt64());
        }
        public bool Equals(VehiclePartChangeCommand other) => Operation == other.Operation && Before.Equals(other.Before) && After.Equals(other.After) && Item == other.Item && Generation == other.Generation && ParentOperation == other.ParentOperation && ParentSocket == other.ParentSocket;
        public override bool Equals(object? other) => other is VehiclePartChangeCommand command && Equals(command);
        public override int GetHashCode() => Operation.GetHashCode();
    }
    public readonly struct VehiclePartChangeReply
    {
        public const int Size = 49;
        public readonly Guid Operation;
        public readonly bool Accepted;
        public readonly uint Revision;
        public readonly VehiclePartState State;
        public readonly ulong Output;
        public VehiclePartChangeReply(Guid operation, bool accepted, uint revision, VehiclePartState state, ulong output = 0)
        { if (operation == Guid.Empty || revision == 0 || output != 0 && (!accepted || output <= WorldRecord.DynamicIdStart)) throw new InvalidDataException("Invalid structural part reply."); Operation = operation; Accepted = accepted; Revision = revision; State = state; Output = output; }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(VehiclePartChangeCommand.Marker); writer.Write(Operation.ToByteArray()); writer.Write(Accepted); writer.Write(Revision); writer.Write(State.Encode()); writer.Write(Output); return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == VehiclePartChangeCommand.Marker;
        public static VehiclePartChangeReply Decode(byte[] payload)
        {
            if (!Is(payload) || payload[17] > 1) throw new InvalidDataException("Invalid structural part reply payload.");
            using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
            return new VehiclePartChangeReply(new Guid(reader.ReadBytes(16)), reader.ReadBoolean(), reader.ReadUInt32(), VehiclePartState.Decode(reader.ReadBytes(19)), reader.ReadUInt64());
        }
    }
}
