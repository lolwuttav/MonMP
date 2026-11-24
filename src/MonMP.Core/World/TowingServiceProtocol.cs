using System;
using System.IO;

namespace MonMP.Core.World
{
    // The native PlayerData field remains durable; this record makes its active-session
    // value and the host store's open status canonical for the multiplayer session.
    public readonly struct TowingServiceState : IEquatable<TowingServiceState>
    {
        public const ushort Adapter = 74;
        public const byte Version = 1;
        public const byte Marker = 0xF7;
        public const int Size = 3;
        public const int UselessVehicleType = 8;
        public const int MaximumVehicleType = 16;
        public static readonly ulong Entity = StaticId.Hash("towing-service/state/v1");

        public byte CurrentVehicleType { get; }
        public bool StoreOpen { get; }
        public bool RecoveryInFlight { get; }
        public bool HasVehicleOnWait => CurrentVehicleType != UselessVehicleType;

        public TowingServiceState(int currentVehicleType, bool storeOpen, bool recoveryInFlight = false)
        {
            if (currentVehicleType < 0 || currentVehicleType > MaximumVehicleType)
                throw new InvalidDataException("Unknown towing-service vehicle type.");
            CurrentVehicleType = (byte)currentVehicleType;
            StoreOpen = storeOpen;
            RecoveryInFlight = recoveryInFlight;
        }

        public byte[] Encode()
        {
            Validate();
            return new[] { Version, CurrentVehicleType, (byte)((StoreOpen ? 1 : 0) | (RecoveryInFlight ? 2 : 0)) };
        }

        public static TowingServiceState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Version || payload[2] > 3)
                throw new InvalidDataException("Invalid towing-service state.");
            return new TowingServiceState(payload[1], (payload[2] & 1) != 0, (payload[2] & 2) != 0);
        }

        public bool Equals(TowingServiceState other) => CurrentVehicleType == other.CurrentVehicleType &&
            StoreOpen == other.StoreOpen && RecoveryInFlight == other.RecoveryInFlight;
        public override bool Equals(object? obj) => obj is TowingServiceState other && Equals(other);
        public override int GetHashCode() => ((CurrentVehicleType * 397) ^ (StoreOpen ? 1 : 0)) * 397 ^ (RecoveryInFlight ? 1 : 0);
        private void Validate() => _ = new TowingServiceState(CurrentVehicleType, StoreOpen, RecoveryInFlight);
    }

    public readonly struct TowingServiceRequest
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public byte VehicleType { get; }

        public TowingServiceRequest(Guid operation, int vehicleType)
        {
            if (operation == Guid.Empty || vehicleType < 0 || vehicleType > TowingServiceState.MaximumVehicleType ||
                vehicleType == TowingServiceState.UselessVehicleType)
                throw new InvalidDataException("Invalid towing-service request.");
            Operation = operation;
            VehicleType = (byte)vehicleType;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(TowingServiceState.Marker);
            writer.Write(VehicleType);
            writer.Write(Operation.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == TowingServiceState.Marker;

        public static TowingServiceRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid towing-service request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            int vehicleType = reader.ReadByte();
            return new TowingServiceRequest(new Guid(reader.ReadBytes(16)), vehicleType);
        }
    }

    public enum TowingServiceStatus : byte
    {
        Accepted = 0,
        NotReady = 1,
        SaveBlocked = 2,
        Capacity = 3,
        WrongService = 4,
        Closed = 5,
        Busy = 6,
        InvalidVehicle = 7,
        Locked = 8,
        UnsupportedVehicle = 9,
        VehicleUnavailable = 10,
        PoliceSeized = 11,
        NotWalking = 12,
        TooFar = 13,
        Funds = 14,
        NativeRejected = 15
    }

    public readonly struct TowingServiceReply
    {
        public const int Size = 18;
        public Guid Operation { get; }
        public TowingServiceStatus Status { get; }

        public TowingServiceReply(Guid operation, TowingServiceStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(TowingServiceStatus), status))
                throw new InvalidDataException("Invalid towing-service reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(TowingServiceState.Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == TowingServiceState.Marker;

        public static TowingServiceReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid towing-service reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            return new TowingServiceReply(new Guid(reader.ReadBytes(16)), (TowingServiceStatus)reader.ReadByte());
        }
    }

    public static class TowingServicePolicy
    {
        public static TowingServiceStatus ValidateHostRequest(bool synchronized, bool saveBlocked, bool requestBudget,
            bool exactService, bool storeOpen, bool alreadyWaiting, bool validVehicleType, bool listingUnlocked,
            bool targetMovementReplicated, bool targetAvailable, bool policeSeized, bool freshWalkingPose,
            bool inRange, bool canAfford)
        {
            if (!synchronized) return TowingServiceStatus.NotReady;
            if (saveBlocked) return TowingServiceStatus.SaveBlocked;
            if (!requestBudget) return TowingServiceStatus.Capacity;
            if (!exactService) return TowingServiceStatus.WrongService;
            if (!storeOpen) return TowingServiceStatus.Closed;
            if (alreadyWaiting) return TowingServiceStatus.Busy;
            if (!validVehicleType) return TowingServiceStatus.InvalidVehicle;
            if (!listingUnlocked) return TowingServiceStatus.Locked;
            if (!targetMovementReplicated) return TowingServiceStatus.UnsupportedVehicle;
            if (!targetAvailable) return TowingServiceStatus.VehicleUnavailable;
            if (policeSeized) return TowingServiceStatus.PoliceSeized;
            if (!freshWalkingPose) return TowingServiceStatus.NotWalking;
            if (!inRange) return TowingServiceStatus.TooFar;
            if (!canAfford) return TowingServiceStatus.Funds;
            return TowingServiceStatus.Accepted;
        }
    }
}
