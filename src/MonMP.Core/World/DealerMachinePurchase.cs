using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum DealerMachineKind : byte
    {
        HugePackager = 1,
        CompostMachine = 2,
        FrenchFriesPress = 3,
        // The dealer's outdoor planter packs, in canonical scene-path order; each stack sells once
        // per loaded world (the native game restocks them on load) (#233).
        OutdoorPlanter1 = 4,
        OutdoorPlanter2 = 5,
        OutdoorPlanter3 = 6,
        OutdoorPlanter4 = 7
    }

    // The host publishes the purchase flag and active state for each exact dealer machine listing.
    public readonly struct DealerMachineState
    {
        public const ushort Adapter = 62;
        public const byte Marker = 0xE6;
        public static readonly ulong HugePackagerEntity = StaticId.Hash("dealer-machine-upgrade/v1/huge-packager");
        public static readonly ulong CompostMachineEntity = StaticId.Hash("dealer-machine-upgrade/v1/compost-machine");
        public static readonly ulong FrenchFriesPressEntity = StaticId.Hash("dealer-machine-upgrade/v1/french-fries-press");

        public DealerMachineKind Kind { get; }
        public bool Purchased { get; }
        public bool DisplayActive { get; }
        public bool MachinePrimaryActive { get; }
        public bool MachineSecondaryActive { get; }

        public DealerMachineState(DealerMachineKind kind, bool purchased, bool displayActive,
            bool machinePrimaryActive, bool machineSecondaryActive = false)
        {
            _ = EntityFor(kind);
            if (kind != DealerMachineKind.HugePackager && machineSecondaryActive)
                throw new InvalidDataException("Unexpected secondary machine object state.");
            Kind = kind;
            Purchased = purchased;
            DisplayActive = displayActive;
            MachinePrimaryActive = machinePrimaryActive;
            MachineSecondaryActive = machineSecondaryActive;
        }

        public static ulong EntityFor(DealerMachineKind kind) => kind switch
        {
            DealerMachineKind.HugePackager => HugePackagerEntity,
            DealerMachineKind.CompostMachine => CompostMachineEntity,
            DealerMachineKind.FrenchFriesPress => FrenchFriesPressEntity,
            >= DealerMachineKind.OutdoorPlanter1 and <= DealerMachineKind.OutdoorPlanter4 =>
                StaticId.Hash("dealer-machine-upgrade/v1/outdoor-planter-" + ((int)kind - (int)DealerMachineKind.OutdoorPlanter1)),
            _ => throw new InvalidDataException("Unknown dealer machine kind.")
        };
        public static bool IsPlanter(DealerMachineKind kind) =>
            kind >= DealerMachineKind.OutdoorPlanter1 && kind <= DealerMachineKind.OutdoorPlanter4;

        public byte[] Encode() => new[] { (byte)Kind, (byte)((Purchased ? 1 : 0) | (DisplayActive ? 2 : 0) |
            (MachinePrimaryActive ? 4 : 0) | (MachineSecondaryActive ? 8 : 0)) };

        public static DealerMachineState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 2 || payload[1] > 15)
                throw new InvalidDataException("Invalid dealer machine state.");
            var kind = (DealerMachineKind)payload[0];
            _ = EntityFor(kind);
            return new DealerMachineState(kind, (payload[1] & 1) != 0, (payload[1] & 2) != 0,
                (payload[1] & 4) != 0, (payload[1] & 8) != 0);
        }
    }

    public readonly struct DealerMachinePurchaseRequest
    {
        public const int Size = 22;
        public DealerMachineKind Kind { get; }
        public Guid Operation { get; }
        public int PriceCents { get; }

        public DealerMachinePurchaseRequest(DealerMachineKind kind, Guid operation, int priceCents)
        {
            _ = DealerMachineState.EntityFor(kind);
            if (operation == Guid.Empty || priceCents <= 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid dealer machine purchase request.");
            Kind = kind;
            Operation = operation;
            PriceCents = priceCents;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(DealerMachineState.Marker);
            writer.Write((byte)Kind);
            writer.Write(Operation.ToByteArray());
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == DealerMachineState.Marker;

        public static DealerMachinePurchaseRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid dealer machine request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var result = new DealerMachinePurchaseRequest((DealerMachineKind)reader.ReadByte(),
                new Guid(reader.ReadBytes(16)), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing dealer machine request data.");
            return result;
        }
    }

    public readonly struct DealerMachinePurchaseReply
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public PurchaseStatus Status { get; }

        public DealerMachinePurchaseReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid dealer machine purchase reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(DealerMachineState.Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == DealerMachineState.Marker;

        public static DealerMachinePurchaseReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid dealer machine reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            byte rawStatus = reader.ReadByte();
            if (!Enum.IsDefined(typeof(PurchaseStatus), rawStatus))
                throw new InvalidDataException("Unknown dealer machine purchase status.");
            return new DealerMachinePurchaseReply(operation, (PurchaseStatus)rawStatus);
        }
    }

    public static class DealerMachinePurchasePolicy
    {
        public static PurchaseStatus ValidateHostRequest(bool stale, bool synchronized, bool actionsBlocked,
            bool requestBudget, bool exactRegisteredDisplay, bool displayActive, bool storeOpen,
            bool bunkerBuilt, bool alreadyPurchased, bool priceMatches, bool freshPlayerPoseAndInRange,
            bool canAfford)
        {
            if (stale || !priceMatches) return PurchaseStatus.PriceChanged;
            if (!requestBudget) return PurchaseStatus.Capacity;
            if (!synchronized || !exactRegisteredDisplay || !displayActive || alreadyPurchased)
                return PurchaseStatus.Unavailable;
            if (actionsBlocked) return PurchaseStatus.NativeRejected;
            if (!storeOpen) return PurchaseStatus.Closed;
            if (!bunkerBuilt) return PurchaseStatus.Unavailable;
            if (!freshPlayerPoseAndInRange) return PurchaseStatus.TooFar;
            if (!canAfford) return PurchaseStatus.Funds;
            return PurchaseStatus.Placed;
        }
    }
}
