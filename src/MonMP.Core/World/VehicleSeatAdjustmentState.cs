using System;
using System.IO;

namespace MonMP.Core.World
{
    /// <summary>Session-only Konig seat position for one exact seat, quantized to the native one-centimeter step.</summary>
    public readonly struct VehicleSeatAdjustmentState
    {
        public const ushort Adapter = 48;
        public readonly short ForwardCentimeters;
        public readonly short HeightCentimeters;
        public readonly uint Generation;

        public VehicleSeatAdjustmentState(short forwardCentimeters, short heightCentimeters, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            ForwardCentimeters = forwardCentimeters;
            HeightCentimeters = heightCentimeters;
            Generation = generation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(8);
            using var writer = new BinaryWriter(stream);
            writer.Write(ForwardCentimeters);
            writer.Write(HeightCentimeters);
            writer.Write(Generation);
            return stream.ToArray();
        }

        public static VehicleSeatAdjustmentState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 8) throw new InvalidDataException("Invalid vehicle seat adjustment state length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            short forward = reader.ReadInt16();
            short height = reader.ReadInt16();
            uint generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid vehicle seat adjustment generation.");
            return new VehicleSeatAdjustmentState(forward, height, generation);
        }

        public static uint NextGeneration(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;
        public bool IsCurrent(VehicleSeatAdjustmentCommand command) => command.ExpectedGeneration == Generation;
    }

    public enum VehicleSeatAdjustmentAxis : byte { Forward = 1, Height = 2 }

    /// <summary>A one-step, host-authorized native seat-slider request.</summary>
    public readonly struct VehicleSeatAdjustmentCommand
    {
        public readonly VehicleSeatAdjustmentAxis Axis;
        public readonly sbyte Direction;
        public readonly uint ExpectedGeneration;

        public VehicleSeatAdjustmentCommand(VehicleSeatAdjustmentAxis axis, int direction, uint expectedGeneration)
        {
            if (!Enum.IsDefined(typeof(VehicleSeatAdjustmentAxis), axis) || (direction != -1 && direction != 1) || expectedGeneration == 0)
                throw new ArgumentException("Invalid vehicle seat adjustment command.");
            Axis = axis;
            Direction = (sbyte)direction;
            ExpectedGeneration = expectedGeneration;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(6);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Axis);
            writer.Write(Direction);
            writer.Write(ExpectedGeneration);
            return stream.ToArray();
        }

        public static VehicleSeatAdjustmentCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 6 || !Enum.IsDefined(typeof(VehicleSeatAdjustmentAxis), payload[0]))
                throw new InvalidDataException("Invalid vehicle seat adjustment command.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var axis = (VehicleSeatAdjustmentAxis)reader.ReadByte();
            sbyte direction = reader.ReadSByte();
            uint generation = reader.ReadUInt32();
            if ((direction != -1 && direction != 1) || generation == 0)
                throw new InvalidDataException("Invalid vehicle seat adjustment command values.");
            return new VehicleSeatAdjustmentCommand(axis, direction, generation);
        }
    }
}
