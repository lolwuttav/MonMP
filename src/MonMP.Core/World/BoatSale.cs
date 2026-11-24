using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public enum BoatSaleKind : byte
    {
        SmallCruiser = 1,
        FishingBoat = 2
    }

    // Shared sign and acquisition presentation for the two one-time boat purchases.
    public readonly struct BoatSaleState
    {
        public const ushort Adapter = 57;
        public const int MaxDisabledObjects = 240;
        public static readonly ulong SmallCruiserEntity = StaticId.Hash("boat-sale/small-cruiser/v1");
        public static readonly ulong FishingBoatEntity = StaticId.Hash("boat-sale/fishing-boat/v1");

        public BoatSaleKind Kind { get; }
        public bool Purchased { get; }
        public bool SignVisible { get; }
        public IReadOnlyList<bool> DisabledObjectsActive { get; }
        public bool FuelCapRootColliderEnabled { get; }
        public bool FuelCapControllerEnabled { get; }
        public bool FuelCapPartEnabled { get; }
        public bool FuelCapPartColliderEnabled { get; }

        public BoatSaleState(BoatSaleKind kind, bool purchased, bool signVisible,
            IEnumerable<bool>? disabledObjectsActive = null, bool fuelCapRootColliderEnabled = false,
            bool fuelCapControllerEnabled = false, bool fuelCapPartEnabled = false,
            bool fuelCapPartColliderEnabled = false)
        {
            ValidateKind(kind);
            var flags = (disabledObjectsActive ?? Array.Empty<bool>()).ToArray();
            if (flags.Length > MaxDisabledObjects || (kind == BoatSaleKind.SmallCruiser && flags.Length != 0) ||
                (kind == BoatSaleKind.FishingBoat && (fuelCapRootColliderEnabled || fuelCapControllerEnabled ||
                    fuelCapPartEnabled || fuelCapPartColliderEnabled)))
                throw new InvalidDataException("Invalid boat sale presentation layout.");
            Kind = kind;
            Purchased = purchased;
            SignVisible = signVisible;
            DisabledObjectsActive = new ReadOnlyCollection<bool>(flags);
            FuelCapRootColliderEnabled = fuelCapRootColliderEnabled;
            FuelCapControllerEnabled = fuelCapControllerEnabled;
            FuelCapPartEnabled = fuelCapPartEnabled;
            FuelCapPartColliderEnabled = fuelCapPartColliderEnabled;
        }

        public static ulong EntityFor(BoatSaleKind kind)
        {
            ValidateKind(kind);
            return kind == BoatSaleKind.SmallCruiser ? SmallCruiserEntity : FishingBoatEntity;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Kind);
            byte flags = (byte)((Purchased ? 1 : 0) | (SignVisible ? 2 : 0) |
                (FuelCapRootColliderEnabled ? 4 : 0) | (FuelCapControllerEnabled ? 8 : 0) |
                (FuelCapPartEnabled ? 16 : 0) | (FuelCapPartColliderEnabled ? 32 : 0));
            writer.Write(flags);
            writer.Write((ushort)DisabledObjectsActive.Count);
            foreach (bool active in DisabledObjectsActive) writer.Write(active);
            return stream.ToArray();
        }

        public static BoatSaleState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 4) throw new InvalidDataException("Invalid boat sale state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var kind = (BoatSaleKind)reader.ReadByte();
            ValidateKind(kind);
            byte flags = reader.ReadByte();
            int count = reader.ReadUInt16();
            if ((flags & ~63) != 0 || count > MaxDisabledObjects || payload.Length != 4 + count)
                throw new InvalidDataException("Invalid boat sale state flags or size.");
            bool[] active = new bool[count];
            for (int i = 0; i < count; i++)
            {
                byte value = reader.ReadByte();
                if (value > 1) throw new InvalidDataException("Invalid boat sale object flag.");
                active[i] = value != 0;
            }
            return new BoatSaleState(kind, (flags & 1) != 0, (flags & 2) != 0, active,
                (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0);
        }

        private static void ValidateKind(BoatSaleKind kind)
        {
            if (kind != BoatSaleKind.SmallCruiser && kind != BoatSaleKind.FishingBoat)
                throw new InvalidDataException("Unknown boat sale kind.");
        }
    }

    public readonly struct BoatSaleRequest
    {
        public const byte Marker = 0xEA;
        public const int Size = 22;
        public BoatSaleKind Kind { get; }
        public Guid Operation { get; }
        public int PriceCents { get; }

        public BoatSaleRequest(BoatSaleKind kind, Guid operation, int priceCents)
        {
            _ = BoatSaleState.EntityFor(kind);
            if (operation == Guid.Empty || priceCents < 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid boat sale request.");
            Kind = kind;
            Operation = operation;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write((byte)Kind);
            writer.Write(Operation.ToByteArray());
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static BoatSaleRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid boat sale request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var result = new BoatSaleRequest((BoatSaleKind)reader.ReadByte(), new Guid(reader.ReadBytes(16)), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing boat sale request data.");
            return result;
        }
    }

    public readonly struct BoatSaleReply
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public PurchaseStatus Status { get; }

        public BoatSaleReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid boat sale reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(BoatSaleRequest.Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == BoatSaleRequest.Marker;

        public static BoatSaleReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid boat sale reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            byte status = reader.ReadByte();
            if (!Enum.IsDefined(typeof(PurchaseStatus), status)) throw new InvalidDataException("Unknown boat sale status.");
            return new BoatSaleReply(operation, (PurchaseStatus)status);
        }
    }

    public static class BoatSalePolicy
    {
        public static int ExpectedPriceCents(BoatSaleKind kind) => kind switch
        {
            BoatSaleKind.SmallCruiser => 1450000,
            BoatSaleKind.FishingBoat => 3000000,
            _ => throw new InvalidDataException("Unknown boat sale kind.")
        };

        public static PurchaseStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestBudget, bool exactRegisteredSign, bool signActive, bool alreadyPurchased,
            bool priceMatches, bool freshPlayerPoseAndInRange, bool canAfford, bool policeClear,
            bool hostFishRequirementsMet)
        {
            if (stale || !priceMatches) return PurchaseStatus.PriceChanged;
            if (!requestBudget) return PurchaseStatus.Capacity;
            if (!synchronized || !exactRegisteredSign || !signActive || alreadyPurchased) return PurchaseStatus.Unavailable;
            if (actionsBlocked) return PurchaseStatus.NativeRejected;
            if (!freshPlayerPoseAndInRange) return PurchaseStatus.TooFar;
            if (!canAfford) return PurchaseStatus.Funds;
            if (!policeClear || !hostFishRequirementsMet) return PurchaseStatus.NativeRejected;
            return PurchaseStatus.Placed;
        }
    }
}
