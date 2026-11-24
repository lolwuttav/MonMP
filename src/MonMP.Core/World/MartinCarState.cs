using System;
using System.IO;

namespace MonMP.Core.World
{
    // Session-only host-authored presentation for Martin's native routed car.
    // CarState uses AI_MartinCar.MartinCar_State's stable declaration order (0..4).
    public sealed class MartinCarState : IEquatable<MartinCarState>
    {
        public const ushort Adapter = 52;
        public const int FixedSize = 45;
        public const int MaxRunningVisuals = 32;

        public readonly byte CarState;
        public readonly ItemPose VehiclePose;
        public readonly uint RunningVisualMask;
        public readonly VehicleWheelSpin WheelSpin;
        public readonly VehiclePassengerSeatState Passenger;

        public MartinCarState(byte carState, ItemPose vehiclePose, uint runningVisualMask, VehicleWheelSpin wheelSpin)
            : this(carState, vehiclePose, runningVisualMask, wheelSpin, new VehiclePassengerSeatState(SeatOccupancy.Unoccupied, 1)) { }

        public MartinCarState(byte carState, ItemPose vehiclePose, uint runningVisualMask, VehicleWheelSpin wheelSpin, VehiclePassengerSeatState passenger)
        {
            CarState = carState;
            VehiclePose = vehiclePose;
            RunningVisualMask = runningVisualMask;
            WheelSpin = wheelSpin;
            Passenger = passenger;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(FixedSize);
            using var writer = new BinaryWriter(stream);
            writer.Write(CarState);
            VehiclePose.Write(writer);
            writer.Write(RunningVisualMask);
            WheelSpin.Write(writer);
            writer.Write(Passenger.Owner);
            writer.Write(Passenger.Generation);
            return stream.ToArray();
        }

        public static MartinCarState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != FixedSize) throw new InvalidDataException("Invalid Martin car state payload.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte carState = reader.ReadByte();
            var pose = ItemPose.Read(reader);
            uint visuals = reader.ReadUInt32();
            var wheels = VehicleWheelSpin.Read(reader);
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid Martin passenger generation.");
            var result = new MartinCarState(carState, pose, visuals, wheels, new VehiclePassengerSeatState(owner, generation));
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Martin car state data.");
            return result;
        }

        private void Validate()
        {
            if (CarState > 4) throw new InvalidDataException("Invalid native Martin car state.");
            VehiclePose.Validate();
            WheelSpin.Validate();
            if (Passenger.Generation == 0) throw new InvalidDataException("Invalid Martin passenger generation.");
        }

        public bool Equals(MartinCarState? other) => other != null && CarState == other.CarState &&
            RunningVisualMask == other.RunningVisualMask && WheelSpin.Equals(other.WheelSpin) &&
            Passenger.Owner == other.Passenger.Owner && Passenger.Generation == other.Passenger.Generation &&
            VehiclePose.X == other.VehiclePose.X && VehiclePose.Y == other.VehiclePose.Y && VehiclePose.Z == other.VehiclePose.Z &&
            VehiclePose.Qx == other.VehiclePose.Qx && VehiclePose.Qy == other.VehiclePose.Qy &&
            VehiclePose.Qz == other.VehiclePose.Qz && VehiclePose.Qw == other.VehiclePose.Qw;
        public override bool Equals(object? obj) => obj is MartinCarState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(CarState, RunningVisualMask, WheelSpin, VehiclePose.X, VehiclePose.Y, VehiclePose.Z, Passenger.Owner, Passenger.Generation);
    }
}
