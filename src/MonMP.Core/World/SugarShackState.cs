using System;
using System.IO;

namespace MonMP.Core.World
{
    [Flags]
    public enum ShackFlags : ushort
    {
        None = 0, ValveEvap = 1, ValveSyrup = 2, Heater = 4, Pump = 8,
        Vacuum = 16, TapWater = 32, TapSyrup = 64, Smoke = 128, Boiling = 256, Built = 512,
        // The host's build ceremony: clearing (everyone moves back), come back, then the animation.
        BuildClearing = 1024, BuildAnimating = 2048, BuildComeBack = BuildClearing | BuildAnimating,
        All = 4095
    }
    public enum ShackBuildPhase : byte { None = 0, Clearing = 1, Animating = 2, ComeBack = 3 }
    [Flags]
    public enum ShackUpgrades : byte
    {
        None = 0, ReverseOsmosis = 1, WaterTank = 2, WaterTank50k = 4, Heater = 8, Siroptier = 16, Crimping = 32, OutdoorPump = 64,
        All = 127
    }

    // The sugar shack: one record for the whole native production line. The host runs the minute tick
    // (vacuum, valves, heating, syrup, barrel and bed-tank filling); guests show its levels, gauges,
    // valves, switches and running effects. Fire is the evaporator's fire power in the last tick.
    public readonly struct SugarShackState : IEquatable<SugarShackState>
    {
        public const ushort Adapter = 27;
        public const int Size = 34;
        public const int MaxTank = 69200, MaxEvaporator = 840, MaxSyrup = 480, MaxLids = 1000000, MaxTrees = 4096, MaxPalette = 64;
        public readonly ShackFlags Flags;
        public readonly ShackUpgrades Upgrades;
        public readonly byte Palette;
        public readonly int Tank, Evaporator, Syrup, Lids;
        public readonly ushort Trees;
        public readonly float Temperature, Fire, Efficiency;
        public SugarShackState(ShackFlags flags, ShackUpgrades upgrades, byte palette, int tank, int evaporator, int syrup, int lids, ushort trees, float temperature, float fire, float efficiency)
        {
            Flags = flags; Upgrades = upgrades; Palette = palette; Tank = tank; Evaporator = evaporator; Syrup = syrup; Lids = lids; Trees = trees;
            Temperature = temperature; Fire = fire; Efficiency = efficiency; Validate();
        }
        public bool Has(ShackFlags flag) => (Flags & flag) == flag;
        public ShackBuildPhase BuildPhase => (ShackBuildPhase)(((ushort)Flags >> 10) & 3);
        public static ShackFlags PhaseFlags(ShackBuildPhase phase) => (ShackFlags)((ushort)phase << 10);
        public bool Has(ShackUpgrades upgrade) => (Upgrades & upgrade) == upgrade;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((ushort)Flags); w.Write((byte)Upgrades); w.Write(Palette); w.Write(Tank); w.Write(Evaporator); w.Write(Syrup); w.Write(Lids);
            w.Write(Trees); w.Write(Temperature); w.Write(Fire); w.Write(Efficiency);
            return stream.ToArray();
        }
        public static SugarShackState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid sugar shack payload.");
            return new SugarShackState((ShackFlags)BitConverter.ToUInt16(payload, 0), (ShackUpgrades)payload[2], payload[3],
                BitConverter.ToInt32(payload, 4), BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12), BitConverter.ToInt32(payload, 16),
                BitConverter.ToUInt16(payload, 20), BitConverter.ToSingle(payload, 22), BitConverter.ToSingle(payload, 26), BitConverter.ToSingle(payload, 30));
        }
        private void Validate()
        {
            if ((Flags & ~ShackFlags.All) != 0 || (Upgrades & ~ShackUpgrades.All) != 0 || Palette > MaxPalette ||
                Tank < 0 || Tank > MaxTank || Evaporator < 0 || Evaporator > MaxEvaporator || Syrup < 0 || Syrup > MaxSyrup || Lids < 0 || Lids > MaxLids || Trees > MaxTrees ||
                !ItemPose.Finite(Temperature, 200) || !ItemPose.Finite(Fire, 1000) || Fire < 0 || !ItemPose.Finite(Efficiency, 2) || Efficiency < 0)
                throw new InvalidDataException("Invalid sugar shack state.");
        }
        public bool Equals(SugarShackState other) => Flags == other.Flags && Upgrades == other.Upgrades && Palette == other.Palette && Tank == other.Tank &&
            Evaporator == other.Evaporator && Syrup == other.Syrup && Lids == other.Lids && Trees == other.Trees &&
            Math.Abs(Temperature - other.Temperature) < 0.05f && Math.Abs(Fire - other.Fire) < 0.05f && Math.Abs(Efficiency - other.Efficiency) < 0.001f;
    }

    public enum ShackOp : byte { ValveEvap = 1, ValveSyrup = 2, HeaterOn = 3, HeaterOff = 4, PumpOn = 5, PumpOff = 6, Heat = 7 }

    // A guest's valve, switch or evaporator fire, sent to the shack record. Heat carries the fire power
    // the guest's burning wood gave the evaporator; the host caps it per shack tick.
    public readonly struct SugarShackCommand
    {
        public const byte Marker = 0xD5;
        public const int Size = 6;
        public const float MaxHeat = 8;
        public readonly ShackOp Op;
        public readonly float Power;
        public SugarShackCommand(ShackOp op, float power = 0) { Op = op; Power = power; Validate(); }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            var payload = new byte[Size]; payload[0] = Marker; payload[1] = (byte)Op;
            BitConverter.GetBytes(Power).CopyTo(payload, 2); return payload;
        }
        public static SugarShackCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid sugar shack command.");
            return new SugarShackCommand((ShackOp)payload[1], BitConverter.ToSingle(payload, 2));
        }
        private void Validate()
        {
            bool ok = Op switch
            {
                ShackOp.Heat => ItemPose.Finite(Power, MaxHeat) && Power > 0,
                ShackOp.ValveEvap or ShackOp.ValveSyrup or ShackOp.HeaterOn or ShackOp.HeaterOff or ShackOp.PumpOn or ShackOp.PumpOff => Power == 0,
                _ => false
            };
            if (!ok) throw new InvalidDataException("Invalid sugar shack command.");
        }
    }

    // A one-time upgrade purchase on the host's syrup-federation counter. The operation identity
    // makes retries idempotent even if the reply is delayed across a world-record revision.
    public readonly struct SugarUpgradeRequest
    {
        public const byte Marker = 0xD6;
        public const int Size = 22;
        public readonly Guid Operation;
        public readonly ShackUpgrades Upgrade;
        public readonly float Price;
        public SugarUpgradeRequest(Guid operation, ShackUpgrades upgrade, float price)
        {
            if (operation == Guid.Empty || upgrade == ShackUpgrades.None || (upgrade & ShackUpgrades.All) != upgrade ||
                (((byte)upgrade & ((byte)upgrade - 1)) != 0) || !ItemPose.Finite(price, 10000000) || price < 0)
                throw new InvalidDataException("Invalid sugar upgrade request.");
            Operation = operation; Upgrade = upgrade; Price = price;
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Upgrade); writer.Write(Operation.ToByteArray()); writer.Write(Price); return stream.ToArray();
        }
        public static SugarUpgradeRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid sugar upgrade request size.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); var upgrade = (ShackUpgrades)reader.ReadByte();
            return new SugarUpgradeRequest(new Guid(reader.ReadBytes(16)), upgrade, reader.ReadSingle());
        }
    }
    public readonly struct SugarUpgradeReply
    {
        public const byte Marker = SugarUpgradeRequest.Marker;
        public const int Size = 18;
        public readonly Guid Operation;
        public readonly PurchaseStatus Status;
        public SugarUpgradeReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid sugar upgrade reply.");
            Operation = operation; Status = status;
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Status); return stream.ToArray();
        }
        public static SugarUpgradeReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid sugar upgrade reply size.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); return new SugarUpgradeReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte());
        }
    }
}
