using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Ephemeral occupancy for ordinary Furniture_Chair and seat-decoration anchors.
    // 143 is intentionally separate from vehicle/extra-seat claims (adapter 36).
    public readonly struct FurnitureSeatState
    {
        public const ushort Adapter = 143;
        public readonly uint Owner;
        public readonly uint Generation;
        public bool Occupied => Owner != SeatOccupancy.Unoccupied;

        public FurnitureSeatState(uint owner, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            Owner = owner;
            Generation = generation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(8);
            using var writer = new BinaryWriter(stream);
            writer.Write(Owner);
            writer.Write(Generation);
            return stream.ToArray();
        }

        public static FurnitureSeatState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 8) throw new InvalidDataException("Invalid furniture seat state length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid furniture seat generation.");
            return new FurnitureSeatState(owner, generation);
        }
    }

    public enum FurnitureSeatAction : byte { Claim = 1, Release = 2, Cancel = 3 }

    // Operation is command-local correlation. It is not occupancy state and is never saved.
    public readonly struct FurnitureSeatCommand
    {
        public const int Size = 21;
        public readonly FurnitureSeatAction Action;
        public readonly uint ExpectedGeneration;
        public readonly Guid Operation;

        public FurnitureSeatCommand(FurnitureSeatAction action, uint expectedGeneration, Guid operation)
        {
            if (!Enum.IsDefined(typeof(FurnitureSeatAction), action) || expectedGeneration == 0 || operation == Guid.Empty)
                throw new ArgumentException("Invalid furniture seat command.");
            Action = action;
            ExpectedGeneration = expectedGeneration;
            Operation = operation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Action);
            writer.Write(ExpectedGeneration);
            writer.Write(Operation.ToByteArray());
            return stream.ToArray();
        }

        public static FurnitureSeatCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || !Enum.IsDefined(typeof(FurnitureSeatAction), payload[0]))
                throw new InvalidDataException("Invalid furniture seat command.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var action = (FurnitureSeatAction)reader.ReadByte();
            uint generation = reader.ReadUInt32();
            var operation = new Guid(reader.ReadBytes(16));
            if (generation == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid furniture seat command fields.");
            return new FurnitureSeatCommand(action, generation, operation);
        }
    }

    public enum FurnitureSeatRefusal : byte
    {
        None = 0,
        Occupied = 1,
        Npc = 2,
        NotWalking = 3,
        Holding = 4,
        TooFar = 5,
        AlreadySeated = 6,
        MissingSeat = 7,
        Moved = 8,
        Stale = 9,
        Cancelled = 10,
        TransfersBlocked = 11,
        NotUsable = 12
    }

    public readonly struct FurnitureSeatReply
    {
        public const int Size = 27;
        public readonly FurnitureSeatAction Action;
        public readonly Guid Operation;
        public readonly bool Accepted;
        public readonly FurnitureSeatRefusal Refusal;
        public readonly FurnitureSeatState State;

        public FurnitureSeatReply(FurnitureSeatAction action, Guid operation, bool accepted, FurnitureSeatRefusal refusal, FurnitureSeatState state)
        {
            if (!Enum.IsDefined(typeof(FurnitureSeatAction), action) || operation == Guid.Empty || !Enum.IsDefined(typeof(FurnitureSeatRefusal), refusal) ||
                accepted != (refusal == FurnitureSeatRefusal.None))
                throw new ArgumentException("Invalid furniture seat reply.");
            Action = action;
            Operation = operation;
            Accepted = accepted;
            Refusal = refusal;
            State = state;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Action);
            writer.Write(Accepted);
            writer.Write((byte)Refusal);
            writer.Write(State.Owner);
            writer.Write(State.Generation);
            return stream.ToArray();
        }

        public static FurnitureSeatReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || !Enum.IsDefined(typeof(FurnitureSeatAction), payload[16]) ||
                payload[17] > 1 || !Enum.IsDefined(typeof(FurnitureSeatRefusal), payload[18]))
                throw new InvalidDataException("Invalid furniture seat reply.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var operation = new Guid(reader.ReadBytes(16));
            var action = (FurnitureSeatAction)reader.ReadByte();
            bool accepted = reader.ReadBoolean();
            var refusal = (FurnitureSeatRefusal)reader.ReadByte();
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            try { return new FurnitureSeatReply(action, operation, accepted, refusal, new FurnitureSeatState(owner, generation)); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid furniture seat reply fields.", ex); }
        }
    }

    public static class FurnitureSeatIdentity
    {
        private const string ScenePrefix = "monmp/furniture-seat/scene/v1/";
        private const string ItemPrefix = "monmp/furniture-seat/item/v1/";

        // Both identity classes are dynamic channel records, so native scene/catalog
        // differences cannot invalidate a fresh guest's static catalog hash.
        public static ulong Scene(string canonicalSeatPath) => Dynamic(ScenePrefix + RequireIdentity(canonicalSeatPath));

        public static ulong Item(ulong itemEntity, string seatRelativePath)
        {
            if (itemEntity <= WorldRecord.DynamicIdStart) throw new ArgumentOutOfRangeException(nameof(itemEntity));
            return Dynamic(ItemPrefix + itemEntity.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + "/" + RequireIdentity(seatRelativePath));
        }

        private static ulong Dynamic(string identity)
        {
            ulong id = StaticId.Hash(identity) | WorldRecord.DynamicIdStart;
            return id == WorldRecord.DynamicIdStart ? id + 1 : id;
        }

        private static string RequireIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity.Length > 2048) throw new ArgumentException("Invalid furniture seat identity.", nameof(identity));
            return identity;
        }
    }

    // Synchronous host-side claim table. Callers invoke it from the WorldChannel command
    // callback, which serializes competing peer requests on the host thread.
    public sealed class FurnitureSeatAuthority
    {
        private readonly SeatRegistry registry = new SeatRegistry();
        private readonly Dictionary<SeatKey, Guid> claimOperations = new Dictionary<SeatKey, Guid>();

        public SeatOccupancy Register(SeatKey seat) => registry.Register(seat);
        public bool TryGet(SeatKey seat, out SeatOccupancy state) => registry.TryGet(seat, out state);
        public bool TryGetSeat(uint peer, out SeatKey seat) => registry.TryGetSeat(peer, out seat);

        public bool TryClaim(SeatKey seat, uint peer, uint expectedGeneration, Guid operation, out SeatOccupancy state)
        {
            state = default;
            if (operation == Guid.Empty) return false;
            if (registry.TryGet(seat, out var current) && current.IsOccupied && current.Peer == peer &&
                current.Generation != 0 && claimOperations.TryGetValue(seat, out var existing) && existing == operation)
            { state = current; return true; }
            if (!registry.TryClaim(seat, peer, expectedGeneration, out state)) return false;
            claimOperations[seat] = operation;
            return true;
        }

        public bool TryRelease(SeatKey seat, uint peer, uint expectedGeneration, out SeatOccupancy state)
        {
            bool released = registry.TryRelease(seat, peer, expectedGeneration, out state);
            if (released) claimOperations.Remove(seat);
            return released;
        }

        // A timed-out claim may have reached the host before its grant reached the guest.
        // Only the operation that owns this generation can cancel that claim.
        public bool TryCancel(SeatKey seat, uint peer, uint expectedGeneration, Guid operation, out SeatOccupancy state)
        {
            state = default;
            if (operation == Guid.Empty || expectedGeneration == 0 || !claimOperations.TryGetValue(seat, out var current) || current != operation ||
                !registry.TryGet(seat, out var occupancy) || occupancy.Peer != peer)
                return false;
            return TryRelease(seat, peer, occupancy.Generation, out state);
        }

        public bool TryReleasePeer(uint peer, out SeatKey seat, out SeatOccupancy state)
        {
            seat = default; state = default;
            if (!registry.TryGetSeat(peer, out seat) || !registry.TryReleasePeer(peer, out state)) return false;
            claimOperations.Remove(seat);
            return true;
        }
    }
}
