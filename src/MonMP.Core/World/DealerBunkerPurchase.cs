using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host-owned one-time bunker purchase and construction presentation. The durable
    // profile remains the save authority; this session record keeps guests in step.
    public readonly struct DealerBunkerPurchaseState : IEquatable<DealerBunkerPurchaseState>
    {
        public const ushort Adapter = 69;
        public const int Size = 5;
        public static readonly ulong Entity = StaticId.Hash("monmp/dealer/bunker-purchase/v1");

        public readonly bool Purchased, SignVisible, ConstructionVisible, Built;
        public readonly int DaySinceBuildStarted;

        public DealerBunkerPurchaseState(bool purchased, bool signVisible, bool constructionVisible, bool built, int daySinceBuildStarted)
        {
            Purchased = purchased;
            SignVisible = signVisible;
            ConstructionVisible = constructionVisible;
            Built = built;
            DaySinceBuildStarted = daySinceBuildStarted;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)((Purchased ? 1 : 0) | (SignVisible ? 2 : 0) | (ConstructionVisible ? 4 : 0) | (Built ? 8 : 0)));
            writer.Write(DaySinceBuildStarted);
            return stream.ToArray();
        }

        public static DealerBunkerPurchaseState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid dealer bunker state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte flags = reader.ReadByte();
            int day = reader.ReadInt32();
            if ((flags & ~15) != 0) throw new InvalidDataException("Invalid dealer bunker state flags.");
            return new DealerBunkerPurchaseState((flags & 1) != 0, (flags & 2) != 0,
                (flags & 4) != 0, (flags & 8) != 0, day);
        }

        public bool Equals(DealerBunkerPurchaseState other) => Purchased == other.Purchased && SignVisible == other.SignVisible &&
            ConstructionVisible == other.ConstructionVisible && Built == other.Built && DaySinceBuildStarted == other.DaySinceBuildStarted;
        public override bool Equals(object? obj) => obj is DealerBunkerPurchaseState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Purchased, SignVisible, ConstructionVisible, Built, DaySinceBuildStarted);

        private void Validate()
        {
            if (DaySinceBuildStarted < -1 || DaySinceBuildStarted > 1_000_000 ||
                (Purchased && DaySinceBuildStarted < 0) || (!Purchased && DaySinceBuildStarted != -1) ||
                (Built && (!Purchased || DaySinceBuildStarted < 7)) || (Built && ConstructionVisible))
                throw new InvalidDataException("Invalid dealer bunker construction state.");
        }
    }

    public readonly struct DealerBunkerPurchaseRequest
    {
        public const byte Marker = 0xF0;
        public const int Size = 21;
        public const int PriceCents = 4_000_000;
        public readonly Guid Operation;
        public readonly int QuotedPriceCents;

        public DealerBunkerPurchaseRequest(Guid operation, int quotedPriceCents)
        {
            if (operation == Guid.Empty || quotedPriceCents < 0 || quotedPriceCents > 1_000_000_000)
                throw new InvalidDataException("Invalid dealer bunker purchase request.");
            Operation = operation;
            QuotedPriceCents = quotedPriceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(QuotedPriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static DealerBunkerPurchaseRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid dealer bunker request size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            return new DealerBunkerPurchaseRequest(new Guid(reader.ReadBytes(16)), reader.ReadInt32());
        }
    }

    public readonly struct DealerBunkerPurchaseReply
    {
        public const byte Marker = 0xF1;
        public const int Size = 18;
        public readonly Guid Operation;
        public readonly PurchaseStatus Status;

        public DealerBunkerPurchaseReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid dealer bunker purchase reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static DealerBunkerPurchaseReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid dealer bunker reply size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            return new DealerBunkerPurchaseReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte());
        }
    }

    public static class DealerBunkerPurchasePolicy
    {
        // Placed means the host may now execute and verify the exact native purchase.
        public static PurchaseStatus Validate(int quotedPriceCents, bool blocked, bool signVisible, bool alreadyPurchased,
            bool dealerUnlocked, bool hydroCompleted, bool inRange, bool canAfford)
        {
            if (quotedPriceCents != DealerBunkerPurchaseRequest.PriceCents) return PurchaseStatus.PriceChanged;
            if (blocked) return PurchaseStatus.NativeRejected;
            if (!signVisible || alreadyPurchased || !dealerUnlocked || !hydroCompleted) return PurchaseStatus.Unavailable;
            if (!inRange) return PurchaseStatus.TooFar;
            if (!canAfford) return PurchaseStatus.Funds;
            return PurchaseStatus.Placed;
        }
    }
}
