using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum NpcWeedPackKind : byte { Small = 1, Big = 2 }

    public enum NpcWeedSaleResult : byte
    {
        Applied = 0,
        Unavailable = 1,
        OutOfRange = 2,
        StalePlayerPose = 3,
        UnsupportedSale = 4,
        NotBuyingToday = 5,
        WrongPackSize = 6,
        DuplicateRequest = 7,
        NotFriendEnough = 8,
        Refused = 9,
        Confiscated = 10
    }

    public readonly struct NpcWeedSaleRequest
    {
        public const byte Marker = 0xB5;
        public const int Size = 38;
        public Guid RequestId { get; }
        public ulong ActorId { get; }
        public ulong ItemId { get; }
        public uint ItemGeneration { get; }
        public NpcWeedPackKind Kind { get; }

        public NpcWeedSaleRequest(Guid requestId, ulong actorId, ulong itemId, uint itemGeneration, NpcWeedPackKind kind)
        {
            if (requestId == Guid.Empty || actorId == 0 || itemId == 0 || itemGeneration == 0 ||
                !Enum.IsDefined(typeof(NpcWeedPackKind), kind))
                throw new InvalidDataException("Invalid NPC weed-sale request.");
            RequestId = requestId; ActorId = actorId; ItemId = itemId; ItemGeneration = itemGeneration; Kind = kind;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(ActorId); writer.Write(ItemId); writer.Write(ItemGeneration);
            writer.Write((byte)Kind); writer.Write(RequestId.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static NpcWeedSaleRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC weed-sale request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte(); ulong actor = reader.ReadUInt64(); ulong item = reader.ReadUInt64();
            uint generation = reader.ReadUInt32(); byte kind = reader.ReadByte(); var request = new Guid(reader.ReadBytes(16));
            if (!Enum.IsDefined(typeof(NpcWeedPackKind), kind)) throw new InvalidDataException("Unknown NPC weed-pack kind.");
            return new NpcWeedSaleRequest(request, actor, item, generation, (NpcWeedPackKind)kind);
        }
    }

    public readonly struct NpcWeedSaleReply
    {
        public const byte Marker = 0xB6;
        public const int Size = 23;
        public Guid RequestId { get; }
        public NpcWeedSaleResult Result { get; }
        public bool FullFriendship { get; }
        public int IncomeCents { get; }

        public NpcWeedSaleReply(Guid requestId, NpcWeedSaleResult result, bool fullFriendship = false, int incomeCents = 0)
        {
            if (requestId == Guid.Empty || !Enum.IsDefined(typeof(NpcWeedSaleResult), result) || incomeCents < 0 || incomeCents > 50000)
                throw new InvalidDataException("Invalid NPC weed-sale reply.");
            RequestId = requestId; Result = result; FullFriendship = fullFriendship; IncomeCents = incomeCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(RequestId.ToByteArray()); writer.Write((byte)Result); writer.Write(FullFriendship);
            writer.Write(IncomeCents);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static NpcWeedSaleReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC weed-sale reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload)); _ = reader.ReadByte();
            var request = new Guid(reader.ReadBytes(16)); byte result = reader.ReadByte();
            if (!Enum.IsDefined(typeof(NpcWeedSaleResult), result)) throw new InvalidDataException("Unknown NPC weed-sale result.");
            byte fullFriendship = reader.ReadByte();
            if (fullFriendship > 1) throw new InvalidDataException("Invalid NPC weed-sale achievement state.");
            return new NpcWeedSaleReply(request, (NpcWeedSaleResult)result, fullFriendship != 0, reader.ReadInt32());
        }
    }

    public static class NpcWeedSalePolicy
    {
        public static bool IsSafeHostSalePath(bool sourceReviewedPlainBuyer, bool buysWeed, bool friendshipSaleBonus,
            bool inheritedTrigger, bool inheritedRefusal, int actorType, bool approvedFriendshipEffects = false) =>
            sourceReviewedPlainBuyer && buysWeed && (!friendshipSaleBonus || approvedFriendshipEffects) && inheritedTrigger && inheritedRefusal && actorType > 0 &&
                actorType < NpcInteractionState.MaxActorTypes;

        public static NpcWeedSaleResult ValidateHostRequest(bool safeSalePath, bool actorActive, bool playerPoseFresh,
            bool itemOwnedAndCurrent, bool actorCanBuy, bool alreadyBoughtToday, bool expectedPackSize,
            bool itemNearPeer, bool itemOverlapsActor, PlayerPose player, float contactX, float contactY, float contactZ,
            float maxDistance)
        {
            if (!safeSalePath) return NpcWeedSaleResult.UnsupportedSale;
            if (!actorActive || !itemOwnedAndCurrent) return NpcWeedSaleResult.Unavailable;
            if (!playerPoseFresh || !FinitePosition(player.X) || !FinitePosition(player.Y) || !FinitePosition(player.Z))
                return NpcWeedSaleResult.StalePlayerPose;
            if (!Finite(contactX) || !Finite(contactY) || !Finite(contactZ) || !Finite(maxDistance) || maxDistance <= 0 || maxDistance > 100)
                return NpcWeedSaleResult.UnsupportedSale;
            double dx = player.X - contactX, dy = player.Y - contactY, dz = player.Z - contactZ;
            if (dx * dx + dy * dy + dz * dz > maxDistance * maxDistance || !itemNearPeer || !itemOverlapsActor)
                return NpcWeedSaleResult.OutOfRange;
            if (!actorCanBuy) return NpcWeedSaleResult.NotFriendEnough;
            if (alreadyBoughtToday) return NpcWeedSaleResult.NotBuyingToday;
            if (!expectedPackSize) return NpcWeedSaleResult.WrongPackSize;
            return NpcWeedSaleResult.Applied;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool FinitePosition(float value) => Finite(value) && Math.Abs((double)value) <= 100000;
    }
}
