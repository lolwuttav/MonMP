using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum BarDrinkStatus : byte
    {
        Accepted = 1,
        Unavailable,
        Closed,
        PriceChanged,
        NotSeated,
        TooFar,
        HandsBusy,
        Funds,
        Cooldown,
        Busy,
        Capacity,
        NativeRejected,
        Uncertain
    }

    // This is the shared, non-persistent bar presentation. Wine is measured in fifths because
    // the native drink consumes exactly 0.2 of a bottle per cup.
    public readonly struct BarDrinkState
    {
        public const ushort Adapter = 68;
        public const int Size = 15;
        public const byte RequestMarker = 0xF8;
        public const byte ReplyMarker = 0xF9;
        public readonly ulong Listing;
        public readonly int PriceCents;
        public readonly bool StoreOpen;
        public readonly bool Drinking;
        public readonly byte BottleCount;
        public readonly byte WineUnits;

        public BarDrinkState(ulong listing, int priceCents, bool storeOpen, bool drinking, byte bottleCount, byte wineUnits)
        {
            if (listing == 0 || priceCents < 0 || priceCents > 1000000000 || wineUnits > 5 || (bottleCount == 0 && wineUnits != 0))
                throw new InvalidDataException("Invalid bar drink state.");
            Listing = listing;
            PriceCents = priceCents;
            StoreOpen = storeOpen;
            Drinking = drinking;
            BottleCount = bottleCount;
            WineUnits = wineUnits;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Listing);
            writer.Write(PriceCents);
            writer.Write((byte)((StoreOpen ? 1 : 0) | (Drinking ? 2 : 0)));
            writer.Write(BottleCount);
            writer.Write(WineUnits);
            return stream.ToArray();
        }

        public static BarDrinkState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid bar drink state size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            ulong listing = reader.ReadUInt64();
            int price = reader.ReadInt32();
            byte flags = reader.ReadByte();
            if ((flags & ~3) != 0) throw new InvalidDataException("Invalid bar drink state flags.");
            return new BarDrinkState(listing, price, (flags & 1) != 0, (flags & 2) != 0, reader.ReadByte(), reader.ReadByte());
        }
    }

    public readonly struct BarDrinkRequest
    {
        public const int Size = 29;
        public readonly Guid Operation;
        public readonly ulong Listing;
        public readonly int PriceCents;

        public BarDrinkRequest(Guid operation, ulong listing, int priceCents)
        {
            if (operation == Guid.Empty || listing == 0 || priceCents < 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid bar drink request.");
            Operation = operation;
            Listing = listing;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(BarDrinkState.RequestMarker);
            writer.Write(Operation.ToByteArray());
            writer.Write(Listing);
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == BarDrinkState.RequestMarker;
        public static bool HasMarker(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == BarDrinkState.RequestMarker;

        public static BarDrinkRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid bar drink request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var request = new BarDrinkRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing bar drink request data.");
            return request;
        }
    }

    public readonly struct BarDrinkReply
    {
        public const int Size = 19;
        public readonly Guid Operation;
        public readonly BarDrinkStatus Status;
        public readonly bool Committed;

        public BarDrinkReply(Guid operation, BarDrinkStatus status, bool committed = false)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(BarDrinkStatus), status) ||
                (committed && status != BarDrinkStatus.Accepted && status != BarDrinkStatus.Uncertain))
                throw new InvalidDataException("Invalid bar drink reply.");
            Operation = operation;
            Status = status;
            Committed = committed;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(BarDrinkState.ReplyMarker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            writer.Write((byte)(Committed ? 1 : 0));
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == BarDrinkState.ReplyMarker;

        public static BarDrinkReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid bar drink reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            Guid operation = new Guid(reader.ReadBytes(16));
            byte status = reader.ReadByte();
            byte committed = reader.ReadByte();
            if (!Enum.IsDefined(typeof(BarDrinkStatus), status) || committed > 1)
                throw new InvalidDataException("Invalid bar drink reply fields.");
            return new BarDrinkReply(operation, (BarDrinkStatus)status, committed != 0);
        }
    }

    public static class BarDrinkPurchasePolicy
    {
        public static BarDrinkStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestCapacity, bool exactTarget, bool exactListing, bool requestedPriceMatches,
            bool storeOpen, bool freshAndNear, bool seated, bool handsFree, bool profileCanDrink,
            bool profileCooldownElapsed, bool barBusy, bool canAfford)
        {
            if (stale || !requestedPriceMatches) return BarDrinkStatus.PriceChanged;
            if (!requestCapacity) return BarDrinkStatus.Capacity;
            if (!synchronized || !exactTarget || !exactListing) return BarDrinkStatus.Unavailable;
            if (actionsBlocked) return BarDrinkStatus.NativeRejected;
            if (!storeOpen) return BarDrinkStatus.Closed;
            if (!freshAndNear) return BarDrinkStatus.TooFar;
            if (!seated) return BarDrinkStatus.NotSeated;
            if (!handsFree) return BarDrinkStatus.HandsBusy;
            if (!profileCanDrink || !profileCooldownElapsed) return BarDrinkStatus.Cooldown;
            if (barBusy) return BarDrinkStatus.Busy;
            if (!canAfford) return BarDrinkStatus.Funds;
            return BarDrinkStatus.Accepted;
        }
    }
}
