using System;
using System.IO;

namespace MonMP.Core.World
{
    /// <summary>Host-owned rear toe settings and odometer display projection for one shared vehicle.</summary>
    public readonly struct VehicleUiState : IEquatable<VehicleUiState>
    {
        public const ushort Adapter = 148;
        private const byte Marker = 0xD8, Version = 1;
        public readonly byte RearLeft, RearRight;
        public readonly float TotalKm;
        public readonly bool HasRearToe;

        public VehicleUiState(int rearLeft, int rearRight, float totalKm, bool hasRearToe = true)
        {
            if (rearLeft < 0 || rearLeft > 100 || rearRight < 0 || rearRight > 100 ||
                float.IsNaN(totalKm) || float.IsInfinity(totalKm) || totalKm < 0 || totalKm > 1000000000f)
                throw new ArgumentOutOfRangeException(nameof(rearLeft), "Invalid shared vehicle UI state.");
            RearLeft = hasRearToe ? (byte)rearLeft : (byte)0; RearRight = hasRearToe ? (byte)rearRight : (byte)0;
            TotalKm = totalKm; HasRearToe = hasRearToe;
        }
        public byte[] Encode()
        {
            var bytes = new byte[9]; bytes[0] = Marker; bytes[1] = Version;
            bytes[2] = HasRearToe ? (byte)1 : (byte)0; bytes[3] = RearLeft; bytes[4] = RearRight;
            BitConverter.GetBytes(TotalKm).CopyTo(bytes, 5);
            return bytes;
        }
        public static VehicleUiState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 9 || payload[0] != Marker || payload[1] != Version || payload[2] > 1 ||
                payload[3] > 100 || payload[4] > 100 || (payload[2] == 0 && (payload[3] != 0 || payload[4] != 0)))
                throw new InvalidDataException("Invalid vehicle UI state.");
            float km = BitConverter.ToSingle(payload, 5);
            if (float.IsNaN(km) || float.IsInfinity(km) || km < 0 || km > 1000000000f)
                throw new InvalidDataException("Invalid vehicle odometer value.");
            return new VehicleUiState(payload[3], payload[4], km, payload[2] != 0);
        }
        public static bool TryDecode(byte[] payload, out VehicleUiState state)
        { try { state = Decode(payload); return true; } catch (InvalidDataException) { state = default; return false; } }
        public bool Equals(VehicleUiState other) => HasRearToe == other.HasRearToe && RearLeft == other.RearLeft && RearRight == other.RearRight && TotalKm.Equals(other.TotalKm);
        public override bool Equals(object? obj) => obj is VehicleUiState other && Equals(other);
        public override int GetHashCode() => ((RearLeft * 397 ^ RearRight) * 397 ^ TotalKm.GetHashCode()) * 2 + (HasRearToe ? 1 : 0);
    }

    public enum VehicleToeSide : byte { RearLeft = 1, RearRight = 2 }
    public readonly struct VehicleToeCommand
    {
        public readonly VehicleToeSide Side;
        public readonly sbyte Direction;
        public readonly byte ExpectedValue;
        public readonly uint Operation;
        public VehicleToeCommand(VehicleToeSide side, int direction, int expectedValue, uint operation = 0)
        {
            if (!Enum.IsDefined(typeof(VehicleToeSide), side) || (direction != -1 && direction != 1) || expectedValue < 0 || expectedValue > 100)
                throw new ArgumentException("Invalid rear toe adjustment command.");
            Side = side; Direction = (sbyte)direction; ExpectedValue = (byte)expectedValue; Operation = operation;
        }
        public byte[] Encode()
        {
            var bytes = new byte[Operation == 0 ? 3 : 7];
            bytes[0] = (byte)Side; bytes[1] = unchecked((byte)Direction); bytes[2] = ExpectedValue;
            if (Operation != 0) BitConverter.GetBytes(Operation).CopyTo(bytes, 3);
            return bytes;
        }
        public static VehicleToeCommand Decode(byte[] payload)
        {
            if (payload == null || (payload.Length != 3 && payload.Length != 7) || !Enum.IsDefined(typeof(VehicleToeSide), payload[0]))
                throw new InvalidDataException("Invalid rear toe adjustment command.");
            uint operation = payload.Length == 7 ? BitConverter.ToUInt32(payload, 3) : 0;
            if (payload.Length == 7 && operation == 0) throw new InvalidDataException("Invalid rear toe adjustment operation.");
            sbyte direction = unchecked((sbyte)payload[1]);
            try { return new VehicleToeCommand((VehicleToeSide)payload[0], direction, payload[2], operation); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid rear toe adjustment command values."); }
        }
    }

    public static class VehicleToeAdjustmentPolicy
    {
        public static bool TryApply(bool host, bool ready, bool saveGate, bool peerWalking, bool inRange,
            VehicleUiState state, VehicleToeCommand command, out VehicleUiState next)
        {
            next = state;
            if (!host || !ready || saveGate || !peerWalking || !inRange) return false;
            if (!state.HasRearToe) return false;
            int value = command.Side == VehicleToeSide.RearLeft ? state.RearLeft : state.RearRight;
            int adjusted = value + command.Direction;
            if (value != command.ExpectedValue || adjusted < 0 || adjusted > 100) return false;
            next = command.Side == VehicleToeSide.RearLeft
                ? new VehicleUiState(adjusted, state.RearRight, state.TotalKm, state.HasRearToe)
                : new VehicleUiState(state.RearLeft, adjusted, state.TotalKm, state.HasRearToe);
            return true;
        }
    }
}
