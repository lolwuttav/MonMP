using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host-owned storefront and Carole friendship gate for the single keg listing.
    public readonly struct KegPurchaseState
    {
        public const ushort Adapter = 60;
        public const int Size = 11;
        public const int MinimumFriendship = 50;

        public readonly ushort ItemPrefab;
        public readonly int PriceCents;
        public readonly int CaroleFriendship;
        public readonly bool Available;
        public readonly bool StoreOpen;

        public KegPurchaseState(ushort itemPrefab, int priceCents, int caroleFriendship, bool available, bool storeOpen)
        {
            if (itemPrefab == 0 || priceCents < 0 || priceCents > 1000000000 || caroleFriendship < 0 || caroleFriendship > 1000000)
                throw new InvalidDataException("Invalid keg purchase state.");
            ItemPrefab = itemPrefab;
            PriceCents = priceCents;
            CaroleFriendship = caroleFriendship;
            Available = available;
            StoreOpen = storeOpen;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(ItemPrefab);
            writer.Write(PriceCents);
            writer.Write(CaroleFriendship);
            writer.Write((byte)((Available ? 1 : 0) | (StoreOpen ? 2 : 0)));
            return stream.ToArray();
        }

        public static KegPurchaseState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid keg purchase state size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            ushort prefab = reader.ReadUInt16();
            int price = reader.ReadInt32();
            int friendship = reader.ReadInt32();
            byte flags = reader.ReadByte();
            if ((flags & ~3) != 0) throw new InvalidDataException("Invalid keg purchase state flags.");
            return new KegPurchaseState(prefab, price, friendship, (flags & 1) != 0, (flags & 2) != 0);
        }
    }

    // The listing and prefab identities are duplicated in the request so a valid operation can
    // never be replayed against a different display or product after scene/catalog drift.
    public readonly struct KegPurchaseRequest
    {
        public const byte Marker = 0xED;
        public const int Size = 31;

        public readonly Guid Operation;
        public readonly ulong Listing;
        public readonly ushort ItemPrefab;
        public readonly int PriceCents;

        public KegPurchaseRequest(Guid operation, ulong listing, ushort itemPrefab, int priceCents)
        {
            if (operation == Guid.Empty || listing == 0 || itemPrefab == 0 || priceCents < 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid keg purchase request.");
            Operation = operation;
            Listing = listing;
            ItemPrefab = itemPrefab;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(Listing);
            writer.Write(ItemPrefab);
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static KegPurchaseRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid keg purchase request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var result = new KegPurchaseRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt16(), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing keg purchase request data.");
            return result;
        }
    }

    public readonly struct KegPurchaseReply
    {
        public const int Size = 26;
        public readonly Guid Operation;
        public readonly PurchaseStatus Status;
        public readonly ulong Item;

        public KegPurchaseReply(Guid operation, PurchaseStatus status, ulong item = 0)
        {
            bool succeeded = status == PurchaseStatus.Held || status == PurchaseStatus.Placed;
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status) ||
                (succeeded ? item <= WorldRecord.DynamicIdStart : item != 0))
                throw new InvalidDataException("Invalid keg purchase reply.");
            Operation = operation;
            Status = status;
            Item = item;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(KegPurchaseRequest.Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            writer.Write(Item);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == KegPurchaseRequest.Marker;

        public static KegPurchaseReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid keg purchase reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            Guid operation = new Guid(reader.ReadBytes(16));
            byte status = reader.ReadByte();
            if (!Enum.IsDefined(typeof(PurchaseStatus), status)) throw new InvalidDataException("Unknown keg purchase result status.");
            return new KegPurchaseReply(operation, (PurchaseStatus)status, reader.ReadUInt64());
        }
    }

    public static class KegPurchasePolicy
    {
        public static PurchaseStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestBudget, bool exactTarget, bool exactListing, bool exactItem,
            int requestedPriceCents, int registeredPriceCents, int hostCaroleFriendship,
            bool listingAvailable, bool storeOpen, bool freshPlayerPoseAndInRange, bool canAfford)
        {
            if (stale || requestedPriceCents != registeredPriceCents) return PurchaseStatus.PriceChanged;
            if (!requestBudget) return PurchaseStatus.Capacity;
            if (!synchronized || !exactTarget || !exactListing || !exactItem || !listingAvailable)
                return PurchaseStatus.Unavailable;
            if (actionsBlocked) return PurchaseStatus.NativeRejected;
            if (hostCaroleFriendship < KegPurchaseState.MinimumFriendship) return PurchaseStatus.Unavailable;
            if (!storeOpen) return PurchaseStatus.Closed;
            if (!freshPlayerPoseAndInRange) return PurchaseStatus.TooFar;
            if (!canAfford) return PurchaseStatus.Funds;
            return PurchaseStatus.Placed;
        }
    }
}
