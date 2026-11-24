using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum NpcGiftKind : byte
    {
        MapleSyrup = 1,
        MapleWine = 2,
        HomemadeCoffee = 3
    }

    public enum NpcGiftResult : byte
    {
        Applied = 0,
        Unavailable = 1,
        OutOfRange = 2,
        StalePlayerPose = 3,
        UnsupportedGift = 4,
        AlreadyGiftedToday = 5,
        DuplicateRequest = 6,
        Refused = 7
    }

    public readonly struct NpcGiftRequest
    {
        public const byte Marker = 0xB3;
        public const int Size = 38;
        public Guid RequestId { get; }
        public ulong ActorId { get; }
        public ulong ItemId { get; }
        public uint ItemGeneration { get; }
        public NpcGiftKind Kind { get; }

        public NpcGiftRequest(Guid requestId, ulong actorId, ulong itemId, uint itemGeneration, NpcGiftKind kind)
        {
            if (requestId == Guid.Empty || actorId == 0 || itemId == 0 || itemGeneration == 0 ||
                !Enum.IsDefined(typeof(NpcGiftKind), kind))
                throw new InvalidDataException("Invalid NPC gift request.");
            RequestId = requestId;
            ActorId = actorId;
            ItemId = itemId;
            ItemGeneration = itemGeneration;
            Kind = kind;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(ActorId);
            writer.Write(ItemId);
            writer.Write(ItemGeneration);
            writer.Write((byte)Kind);
            writer.Write(RequestId.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static NpcGiftRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC gift request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte();
            ulong actorId = reader.ReadUInt64();
            ulong itemId = reader.ReadUInt64();
            uint generation = reader.ReadUInt32();
            byte kind = reader.ReadByte();
            var requestId = new Guid(reader.ReadBytes(16));
            if (!Enum.IsDefined(typeof(NpcGiftKind), kind)) throw new InvalidDataException("Unknown NPC gift kind.");
            return new NpcGiftRequest(requestId, actorId, itemId, generation, (NpcGiftKind)kind);
        }
    }

    public readonly struct NpcGiftReply
    {
        public const byte Marker = 0xB4;
        public const int Size = 19;
        public Guid RequestId { get; }
        public NpcGiftResult Result { get; }
        public bool FullFriendship { get; }

        public NpcGiftReply(Guid requestId, NpcGiftResult result, bool fullFriendship = false)
        {
            if (requestId == Guid.Empty || !Enum.IsDefined(typeof(NpcGiftResult), result))
                throw new InvalidDataException("Invalid NPC gift reply.");
            RequestId = requestId;
            Result = result; FullFriendship = fullFriendship;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(RequestId.ToByteArray());
            writer.Write((byte)Result);
            writer.Write(FullFriendship);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static NpcGiftReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC gift reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte();
            var requestId = new Guid(reader.ReadBytes(16));
            byte result = reader.ReadByte();
            if (!Enum.IsDefined(typeof(NpcGiftResult), result)) throw new InvalidDataException("Unknown NPC gift result.");
            byte fullFriendship = reader.ReadByte();
            if (fullFriendship > 1) throw new InvalidDataException("Invalid NPC gift achievement state.");
            return new NpcGiftReply(requestId, (NpcGiftResult)result, fullFriendship != 0);
        }
    }

    public static class NpcGiftPolicy
    {
        // Native gifts are item-trigger contacts, not clicks on an actor's pivot.
        // Use the same bounded reach as accepted held-item poses; an NPC's collider
        // may extend beyond its transform and must not shorten this allowance.
        public const float HeldItemReach = 4.5f;
        // Actor_Type.Carole is 26 in the reconstructed game enum. Coffee is the
        // only reviewed Carole gift and must not be accepted for another actor.
        public const int CaroleActorTypeId = 26;

        public static bool IsSafeHostGiftPath(bool isActor, bool baseTriggerHandler, bool approvedAcceptancePath,
            int actorType, NpcGiftKind kind) =>
            isActor && baseTriggerHandler && approvedAcceptancePath && actorType > 0 &&
            actorType < NpcInteractionState.MaxActorTypes && Enum.IsDefined(typeof(NpcGiftKind), kind) &&
            (kind != NpcGiftKind.HomemadeCoffee || actorType == CaroleActorTypeId);

        public static NpcGiftResult ValidateHostRequest(bool safeGiftPath, bool actorActive, bool playerPoseFresh,
            bool itemOwnedAndCurrent, bool actorCanReceive, bool alreadyGiftedToday, bool itemNearPeer,
            bool itemOverlapsActor, PlayerPose player, float contactX, float contactY, float contactZ, float maxDistance)
        {
            if (!safeGiftPath) return NpcGiftResult.UnsupportedGift;
            if (!actorActive || !itemOwnedAndCurrent) return NpcGiftResult.Unavailable;
            if (!playerPoseFresh) return NpcGiftResult.StalePlayerPose;
            if (!FinitePosition(player.X) || !FinitePosition(player.Y) || !FinitePosition(player.Z))
                return NpcGiftResult.StalePlayerPose;
            if (!Finite(contactX) || !Finite(contactY) || !Finite(contactZ) || !Finite(maxDistance) ||
                maxDistance <= 0 || maxDistance > 100) return NpcGiftResult.UnsupportedGift;
            double dx = player.X - contactX, dy = player.Y - contactY, dz = player.Z - contactZ;
            if (dx * dx + dy * dy + dz * dz > maxDistance * maxDistance || !itemNearPeer || !itemOverlapsActor)
                return NpcGiftResult.OutOfRange;
            if (!actorCanReceive) return NpcGiftResult.Unavailable;
            if (alreadyGiftedToday) return NpcGiftResult.AlreadyGiftedToday;
            return NpcGiftResult.Applied;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool FinitePosition(float value) => Finite(value) && Math.Abs((double)value) <= 100000;
    }
}
