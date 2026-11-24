using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host-owned state for one exact native VehicleForSale_Display listing. The optional pose
    // records the native clone's initial pose so late joiners can reconstruct the purchased asset.
    public readonly struct VehicleSaleListingState
    {
        public const ushort Adapter = 67;
        public const byte Marker = 0xF6;
        public const int MaximumPriceCents = 1000000000;

        public readonly int NativeVehicleType;
        public readonly int PriceCents;
        public readonly ulong CatalogFingerprint;
        public readonly bool Available;
        public readonly bool StoreOpen;
        public readonly bool Purchased;
        public readonly ulong VehicleEntity;
        public readonly ItemPose? VehiclePose;

        public VehicleSaleListingState(int nativeVehicleType, int priceCents, ulong catalogFingerprint,
            bool available, bool storeOpen, bool purchased, ulong vehicleEntity = 0, ItemPose? vehiclePose = null)
        {
            if (nativeVehicleType < 0 || nativeVehicleType > 255 || priceCents < 0 ||
                priceCents > MaximumPriceCents || catalogFingerprint == 0 ||
                (purchased ? vehicleEntity <= WorldRecord.DynamicIdStart || !vehiclePose.HasValue : vehicleEntity != 0 || vehiclePose.HasValue))
                throw new InvalidDataException("Invalid vehicle sale listing state.");
            vehiclePose?.Validate();
            NativeVehicleType = nativeVehicleType;
            PriceCents = priceCents;
            CatalogFingerprint = catalogFingerprint;
            Available = available;
            StoreOpen = storeOpen;
            Purchased = purchased;
            VehicleEntity = vehicleEntity;
            VehiclePose = vehiclePose;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(NativeVehicleType);
            writer.Write(PriceCents);
            writer.Write(CatalogFingerprint);
            writer.Write((byte)((Available ? 1 : 0) | (StoreOpen ? 2 : 0) | (Purchased ? 4 : 0)));
            writer.Write(VehicleEntity);
            if (VehiclePose.HasValue) VehiclePose.Value.Write(writer);
            return stream.ToArray();
        }

        public static VehicleSaleListingState Decode(byte[] payload)
        {
            if (payload == null || (payload.Length != 26 && payload.Length != 54))
                throw new InvalidDataException("Invalid vehicle sale listing state size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid vehicle sale listing marker.");
            int type = reader.ReadInt32();
            int price = reader.ReadInt32();
            ulong fingerprint = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            if ((flags & ~7) != 0) throw new InvalidDataException("Invalid vehicle sale listing flags.");
            ulong entity = reader.ReadUInt64();
            bool purchased = (flags & 4) != 0;
            if (purchased != (payload.Length == 54)) throw new InvalidDataException("Vehicle sale purchase pose is missing or unexpected.");
            ItemPose? pose = purchased ? ItemPose.Read(reader) : (ItemPose?)null;
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing vehicle sale listing data.");
            return new VehicleSaleListingState(type, price, fingerprint, (flags & 1) != 0,
                (flags & 2) != 0, purchased, entity, pose);
        }
    }

    public readonly struct VehicleSaleRequest
    {
        public const int Size = 33;
        public const byte Marker = VehicleSaleListingState.Marker;
        public readonly Guid Operation;
        public readonly ulong Listing;
        public readonly int NativeVehicleType;
        public readonly int PriceCents;

        public VehicleSaleRequest(Guid operation, ulong listing, int nativeVehicleType, int priceCents)
        {
            if (operation == Guid.Empty || listing == 0 || nativeVehicleType < 0 || nativeVehicleType > 255 ||
                priceCents < 0 || priceCents > VehicleSaleListingState.MaximumPriceCents)
                throw new InvalidDataException("Invalid vehicle sale request.");
            Operation = operation;
            Listing = listing;
            NativeVehicleType = nativeVehicleType;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(Listing);
            writer.Write(NativeVehicleType);
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static VehicleSaleRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid vehicle sale request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var request = new VehicleSaleRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing vehicle sale request data.");
            return request;
        }
    }

    public readonly struct VehicleSaleReply
    {
        public const int Size = 26;
        public const byte Marker = VehicleSaleRequest.Marker;
        public readonly Guid Operation;
        public readonly PurchaseStatus Status;
        public readonly ulong VehicleEntity;

        public VehicleSaleReply(Guid operation, PurchaseStatus status, ulong vehicleEntity = 0)
        {
            bool success = status == PurchaseStatus.Placed;
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status) ||
                (success ? vehicleEntity <= WorldRecord.DynamicIdStart : vehicleEntity != 0))
                throw new InvalidDataException("Invalid vehicle sale reply.");
            Operation = operation;
            Status = status;
            VehicleEntity = vehicleEntity;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            writer.Write(VehicleEntity);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static VehicleSaleReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid vehicle sale reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var reply = new VehicleSaleReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte(), reader.ReadUInt64());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing vehicle sale reply data.");
            return reply;
        }
    }

    public static class VehicleSalePolicy
    {
        public static PurchaseStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestBudget, bool exactRegisteredListing, bool listingAvailable, bool storeOpen,
            bool hostPrerequisitesMet, bool priceMatches, bool typeMatches, bool freshPlayerPoseAndInRange,
            bool buyerHandsFree, bool canAfford)
        {
            if (stale || !priceMatches) return PurchaseStatus.PriceChanged;
            if (!requestBudget) return PurchaseStatus.Capacity;
            if (!synchronized || !exactRegisteredListing || !listingAvailable || !typeMatches)
                return PurchaseStatus.Unavailable;
            if (actionsBlocked) return PurchaseStatus.NativeRejected;
            if (!storeOpen) return PurchaseStatus.Closed;
            if (!hostPrerequisitesMet) return PurchaseStatus.Unavailable;
            if (!freshPlayerPoseAndInRange) return PurchaseStatus.TooFar;
            if (!buyerHandsFree) return PurchaseStatus.HandsBusy;
            if (!canAfford) return PurchaseStatus.Funds;
            return PurchaseStatus.Placed;
        }
    }
}
