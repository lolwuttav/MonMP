using System;
using System.IO;

namespace MonMP.Core.World
{
    // The six-bay storage garage is purchased from its native sign and unlocks one host-save ToDo.
    public readonly struct StorageGarageSaleState
    {
        public const ushort Adapter = 82;
        public const int PurchasePriceCents = 2400000;
        private const byte Marker = 0xA6;
        public static readonly ulong Entity = StaticId.Hash("garage-sale/six-bay/v1");

        public bool Purchased { get; }
        public bool SignVisible { get; }

        public StorageGarageSaleState(bool purchased, bool signVisible)
        {
            Purchased = purchased;
            SignVisible = signVisible;
        }

        public byte[] Encode() => new[] { Marker, (byte)((Purchased ? 1 : 0) | (SignVisible ? 2 : 0)) };

        public static StorageGarageSaleState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 2 || payload[0] != Marker || (payload[1] & ~3) != 0)
                throw new InvalidDataException("Invalid six-bay garage sale state.");
            return new StorageGarageSaleState((payload[1] & 1) != 0, (payload[1] & 2) != 0);
        }
    }

    public readonly struct StorageGarageSaleRequest
    {
        public const byte Marker = 0xFA;
        public const int Size = 21;
        public Guid Operation { get; }
        public int PriceCents { get; }

        public StorageGarageSaleRequest(Guid operation, int priceCents)
        {
            if (operation == Guid.Empty || priceCents < 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid six-bay garage purchase request.");
            Operation = operation;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static StorageGarageSaleRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid six-bay garage request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var request = new StorageGarageSaleRequest(new Guid(reader.ReadBytes(16)), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing six-bay garage request data.");
            return request;
        }
    }

    public readonly struct StorageGarageSaleReply
    {
        public const byte Marker = 0xFB;
        public const int Size = 18;
        public Guid Operation { get; }
        public PurchaseStatus Status { get; }

        public StorageGarageSaleReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid six-bay garage purchase reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static StorageGarageSaleReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid six-bay garage reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var reply = new StorageGarageSaleReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing six-bay garage reply data.");
            return reply;
        }
    }

    public static class StorageGarageSalePolicy
    {
        public static PurchaseStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestBudget, bool exactRegisteredSign, bool signActive, bool alreadyPurchased,
            bool priceMatches, bool freshPlayerPoseAndInRange, bool canAfford, bool policeClear)
        {
            if (stale || !priceMatches) return PurchaseStatus.PriceChanged;
            if (!requestBudget) return PurchaseStatus.Capacity;
            if (!synchronized || !exactRegisteredSign || !signActive || alreadyPurchased)
                return PurchaseStatus.Unavailable;
            if (actionsBlocked || !policeClear) return PurchaseStatus.NativeRejected;
            if (!freshPlayerPoseAndInRange) return PurchaseStatus.TooFar;
            if (!canAfford) return PurchaseStatus.Funds;
            return PurchaseStatus.Placed;
        }
    }
}
