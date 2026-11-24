using System;
using System.IO;

namespace MonMP.Core.World
{
    // Session-only occupancy of the Konig passenger seat. The vehicle's driver
    // lease remains in VehicleState because it also owns continuous physics.
    public readonly struct VehiclePassengerSeatState
    {
        public const ushort Adapter = 36;
        public readonly uint Owner;
        public readonly uint Generation;
        public bool Occupied => Owner != SeatOccupancy.Unoccupied;

        public VehiclePassengerSeatState(uint owner, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            Owner = owner;
            Generation = generation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(8);
            using var writer = new BinaryWriter(stream);
            writer.Write(Owner);
            writer.Write(Generation);
            return stream.ToArray();
        }

        public static VehiclePassengerSeatState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 8) throw new InvalidDataException("Invalid passenger seat state length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid passenger seat generation.");
            return new VehiclePassengerSeatState(owner, generation);
        }
    }

    public enum VehiclePassengerSeatAction : byte { Enter = 1, Exit = 2 }

    public readonly struct VehiclePassengerSeatCommand
    {
        public readonly VehiclePassengerSeatAction Action;
        public readonly uint ExpectedGeneration;

        public VehiclePassengerSeatCommand(VehiclePassengerSeatAction action, uint expectedGeneration)
        {
            if (!Enum.IsDefined(typeof(VehiclePassengerSeatAction), action) || expectedGeneration == 0)
                throw new ArgumentException("Invalid passenger seat command.");
            Action = action;
            ExpectedGeneration = expectedGeneration;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(5);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Action);
            writer.Write(ExpectedGeneration);
            return stream.ToArray();
        }

        public static VehiclePassengerSeatCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 5 || !Enum.IsDefined(typeof(VehiclePassengerSeatAction), payload[0]))
                throw new InvalidDataException("Invalid passenger seat command.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            uint generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid passenger seat command generation.");
            return new VehiclePassengerSeatCommand((VehiclePassengerSeatAction)payload[0], generation);
        }
    }
}
