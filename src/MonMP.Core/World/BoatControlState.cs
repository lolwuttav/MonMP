using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum BoatControlKind : byte
    {
        Anchor = 1,
        FishingBoatIgnition = 2,
        SmallCruiserFuelCap = 3,
        SmallCruiserIgnition = 4
    }

    public enum BoatControlAction : byte
    {
        SetAnchor = 1,
        ActivateSmallCruiserFuelCap = 2,
        IgnitionMouseDown = 3,
        IgnitionMouseUp = 4
    }

    // Static, session-scoped controls for the two stock boats. Boat movement and steering are
    // separate work; this record only carries the native control state represented by each kind.
    public readonly struct BoatControlState : IEquatable<BoatControlState>
    {
        public const ushort Adapter = 56;
        public readonly BoatControlKind Kind;
        public readonly byte Value;

        public BoatControlState(BoatControlKind kind, byte value)
        {
            Validate(kind, value);
            Kind = kind;
            Value = value;
        }

        public byte[] Encode() => new[] { (byte)Kind, Value };

        public static BoatControlState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 2) throw new InvalidDataException("Invalid boat control payload.");
            return new BoatControlState((BoatControlKind)payload[0], payload[1]);
        }

        private static void Validate(BoatControlKind kind, byte value)
        {
            if (!Enum.IsDefined(typeof(BoatControlKind), kind) ||
                (kind == BoatControlKind.FishingBoatIgnition || kind == BoatControlKind.SmallCruiserIgnition ? value > 2 : value > 1))
                throw new InvalidDataException("Invalid boat control state.");
        }

        public bool Equals(BoatControlState other) => Kind == other.Kind && Value == other.Value;
        public override bool Equals(object? obj) => obj is BoatControlState other && Equals(other);
        public override int GetHashCode() => ((int)Kind << 8) | Value;
        public static bool operator ==(BoatControlState left, BoatControlState right) => left.Equals(right);
        public static bool operator !=(BoatControlState left, BoatControlState right) => !left.Equals(right);
    }

    // Small bounded commands: anchor requests carry the desired state, fuel-cap requests can only
    // activate the native cap, and ignition requests preserve the native press/release gesture.
    public readonly struct BoatControlCommand
    {
        public const int Size = 3;
        public readonly BoatControlKind Kind;
        public readonly BoatControlAction Action;
        public readonly byte Value;

        public BoatControlCommand(BoatControlKind kind, BoatControlAction action, byte value = 0)
        {
            Validate(kind, action, value);
            Kind = kind;
            Action = action;
            Value = value;
        }

        public byte[] Encode() => new[] { (byte)Kind, (byte)Action, Value };

        public static BoatControlCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid boat control command.");
            return new BoatControlCommand((BoatControlKind)payload[0], (BoatControlAction)payload[1], payload[2]);
        }

        private static void Validate(BoatControlKind kind, BoatControlAction action, byte value)
        {
            if (!Enum.IsDefined(typeof(BoatControlKind), kind) || !Enum.IsDefined(typeof(BoatControlAction), action))
                throw new InvalidDataException("Invalid boat control command.");
            bool valid = kind switch
            {
                BoatControlKind.Anchor => action == BoatControlAction.SetAnchor && value <= 1,
                BoatControlKind.SmallCruiserFuelCap => action == BoatControlAction.ActivateSmallCruiserFuelCap && value == 0,
                BoatControlKind.FishingBoatIgnition => (action == BoatControlAction.IgnitionMouseDown || action == BoatControlAction.IgnitionMouseUp) && value == 0,
                BoatControlKind.SmallCruiserIgnition => (action == BoatControlAction.IgnitionMouseDown || action == BoatControlAction.IgnitionMouseUp) && value == 0,
                _ => false
            };
            if (!valid) throw new InvalidDataException("Boat control action does not match its target.");
        }
    }
}
