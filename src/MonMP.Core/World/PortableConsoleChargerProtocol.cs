using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum PortableConsoleLocation : byte
    {
        Hand = 0,
        HomeCharger = 1,
        SpeedwayCharger = 2
    }

    public enum PortableConsoleChargerAction : byte
    {
        Place = 1,
        Retrieve = 2
    }

    public enum PortableConsoleChargerStatus : byte
    {
        Accepted = 0,
        NotReady = 1,
        SaveBlocked = 2,
        Capacity = 3,
        UnknownCharger = 4,
        Inactive = 5,
        NotWalking = 6,
        TooFar = 7,
        WrongState = 8,
        HandsBusy = 9,
        NativeRejected = 10
    }

    // One host-owned console state is repeated on each exact native charger record. The
    // vanilla save remains durable; this record is the active-session authority.
    public readonly struct PortableConsoleChargerState : IEquatable<PortableConsoleChargerState>
    {
        public const ushort Adapter = 65;
        public const byte Version = 1;
        public const byte Marker = 0xF5;
        public const int Size = 5;

        public PortableConsoleLocation Location { get; }
        public byte BatteryLife { get; }
        public ushort ChargingTicks { get; }

        public PortableConsoleChargerState(PortableConsoleLocation location, int batteryLife, int chargingTicks)
        {
            if (!Enum.IsDefined(typeof(PortableConsoleLocation), location) || batteryLife < 0 || batteryLife > 3 || chargingTicks < 0 || chargingTicks >= 180)
                throw new InvalidDataException("Invalid portable console charger state.");
            if (batteryLife == 3 && chargingTicks != 0)
                throw new InvalidDataException("A full portable console cannot retain partial charging ticks.");
            Location = location;
            BatteryLife = (byte)batteryLife;
            ChargingTicks = (ushort)chargingTicks;
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Version);
            writer.Write((byte)Location);
            writer.Write(BatteryLife);
            writer.Write(ChargingTicks);
            return stream.ToArray();
        }

        public static PortableConsoleChargerState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size)
                throw new InvalidDataException("Invalid portable console charger state size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            byte version = reader.ReadByte();
            if (version != Version) throw new InvalidDataException("Unsupported portable console charger state version.");
            return new PortableConsoleChargerState((PortableConsoleLocation)reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16());
        }

        public bool Equals(PortableConsoleChargerState other) => Location == other.Location && BatteryLife == other.BatteryLife && ChargingTicks == other.ChargingTicks;
        public override bool Equals(object? obj) => obj is PortableConsoleChargerState other && Equals(other);
        public override int GetHashCode() => (((int)Location * 397) ^ BatteryLife) * 397 ^ ChargingTicks;
        private void Validate() => _ = new PortableConsoleChargerState(Location, BatteryLife, ChargingTicks);
    }

    public readonly struct PortableConsoleChargerRequest
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public PortableConsoleChargerAction Action { get; }

        public PortableConsoleChargerRequest(Guid operation, PortableConsoleChargerAction action)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PortableConsoleChargerAction), action))
                throw new InvalidDataException("Invalid portable console charger request.");
            Operation = operation;
            Action = action;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(PortableConsoleChargerState.Marker);
            writer.Write((byte)Action);
            writer.Write(Operation.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == PortableConsoleChargerState.Marker;

        public static PortableConsoleChargerRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid portable console charger request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var action = (PortableConsoleChargerAction)reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            return new PortableConsoleChargerRequest(operation, action);
        }
    }

    public readonly struct PortableConsoleChargerReply
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public PortableConsoleChargerStatus Status { get; }

        public PortableConsoleChargerReply(Guid operation, PortableConsoleChargerStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PortableConsoleChargerStatus), status))
                throw new InvalidDataException("Invalid portable console charger reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(PortableConsoleChargerState.Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == PortableConsoleChargerState.Marker;

        public static PortableConsoleChargerReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid portable console charger reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            return new PortableConsoleChargerReply(operation, (PortableConsoleChargerStatus)reader.ReadByte());
        }
    }

    public static class PortableConsoleChargerPolicy
    {
        public static PortableConsoleChargerStatus ValidateHostRequest(bool synchronized, bool saveBlocked, bool requestBudget,
            bool exactRegisteredCharger, bool chargerActive, bool freshWalkingPose, bool inRange, bool handsFree,
            PortableConsoleChargerAction action, PortableConsoleLocation currentLocation, PortableConsoleLocation chargerLocation)
        {
            if (!synchronized) return PortableConsoleChargerStatus.NotReady;
            if (saveBlocked) return PortableConsoleChargerStatus.SaveBlocked;
            if (!requestBudget) return PortableConsoleChargerStatus.Capacity;
            if (!Enum.IsDefined(typeof(PortableConsoleLocation), chargerLocation) || chargerLocation == PortableConsoleLocation.Hand ||
                !Enum.IsDefined(typeof(PortableConsoleLocation), currentLocation) || !Enum.IsDefined(typeof(PortableConsoleChargerAction), action))
                return PortableConsoleChargerStatus.UnknownCharger;
            if (!exactRegisteredCharger) return PortableConsoleChargerStatus.UnknownCharger;
            if (!chargerActive) return PortableConsoleChargerStatus.Inactive;
            if (!freshWalkingPose) return PortableConsoleChargerStatus.NotWalking;
            if (!inRange) return PortableConsoleChargerStatus.TooFar;

            if (action == PortableConsoleChargerAction.Place)
            {
                if (currentLocation != PortableConsoleLocation.Hand) return PortableConsoleChargerStatus.WrongState;
                if (!handsFree) return PortableConsoleChargerStatus.HandsBusy;
            }
            else if (currentLocation != chargerLocation)
            {
                return PortableConsoleChargerStatus.WrongState;
            }
            return PortableConsoleChargerStatus.Accepted;
        }
    }
}
