using System;
using System.IO;

namespace MonMP.Core.World
{
    // One part socket. Native GameObjectData values are synchronized for the stock Konig steering
    // rack, adjustable coilovers/control arms, rear-seat fold, gauge settings, turbo wastegate,
    // stake/spoiler pickup history, and a wheel's mounted tire (IntValue1, the native TireType;
    // pressure has its own adapter);
    // paint and per-hole bolts have separate adapters.
    public readonly struct VehiclePartState : IEquatable<VehiclePartState>
    {
        public const ushort Adapter = 17;
        public const int EncodedSize = 19;
        public const float MaxNativeValue = 1000000f;
        public const int MaxNativeInteger = 1000000;
        public readonly bool Installed, FullyTightened;
        public readonly ushort Prefab;
        public readonly float Value1, Value2;
        public readonly int IntValue1, IntValue2;
        public static readonly VehiclePartState Empty = new VehiclePartState(false, 0, false);
        public VehiclePartState(bool installed, ushort prefab, bool fullyTightened,
            float value1 = 0, float value2 = 0, int intValue1 = 0, int intValue2 = 0)
        {
            if ((!installed && (prefab != 0 || fullyTightened || value1 != 0 || value2 != 0 || intValue1 != 0 || intValue2 != 0)) ||
                (installed && prefab == 0)) throw new InvalidDataException("Invalid vehicle part state.");
            Installed = installed; Prefab = installed ? prefab : (ushort)0; FullyTightened = installed && fullyTightened;
            Value1 = value1; Value2 = value2; IntValue1 = intValue1; IntValue2 = intValue2;
            Validate();
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)((Installed ? 1 : 0) | (FullyTightened ? 2 : 0))); w.Write(Prefab);
            w.Write(Value1); w.Write(Value2); w.Write(IntValue1); w.Write(IntValue2);
            return stream.ToArray();
        }
        public static VehiclePartState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != EncodedSize || payload[0] > 3) throw new InvalidDataException("Invalid vehicle part payload.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            byte flags = r.ReadByte(); ushort prefab = r.ReadUInt16();
            var state = new VehiclePartState((flags & 1) != 0, prefab, (flags & 2) != 0,
                r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing vehicle part payload.");
            return state;
        }
        public bool HasNativeValues => Value1 != 0 || Value2 != 0 || IntValue1 != 0 || IntValue2 != 0;
        public bool HasValidSteeringRackValues => Installed && Whole(Value1, 20, 70) && Whole(Value2, 5, 35) &&
            IntValue1 >= 0 && IntValue1 <= 100 && IntValue2 >= 0 && IntValue2 <= 100;
        public const int MaxTireType = 6; // native TireType: None..Drag
        public bool HasValidWheelValues => Installed && Value1 == 0 && Value2 == 0 && IntValue2 == 0 &&
            IntValue1 >= 0 && IntValue1 <= MaxTireType;
        private static bool Whole(float value, float min, float max) => Finite(value) && value >= min && value <= max && value == MathF.Round(value);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxNativeValue;
        private void Validate()
        {
            if (!Finite(Value1) || !Finite(Value2) || Math.Abs((long)IntValue1) > MaxNativeInteger || Math.Abs((long)IntValue2) > MaxNativeInteger)
                throw new InvalidDataException("Invalid vehicle part state.");
        }
        public bool Equals(VehiclePartState other) => Installed == other.Installed && Prefab == other.Prefab &&
            FullyTightened == other.FullyTightened && Value1.Equals(other.Value1) && Value2.Equals(other.Value2) &&
            IntValue1 == other.IntValue1 && IntValue2 == other.IntValue2;
        public override bool Equals(object? obj) => obj is VehiclePartState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Installed, FullyTightened, Prefab, Value1, Value2, IntValue1, IntValue2);
    }

    // Request policy for host-authored part state. The supported native-value mutation is one
    // discrete step from a real steering-rack or suspension knob, or a rear-seat fold toggle.
    // A wheel carries its tire, fixed while installed (tires are changed off the car);
    // other socket payloads must carry zero values.
    public static class VehiclePartCommandPolicy
    {
        // boltless: the socket has no bolt holes (a shifter knob), so a part is fully tightened the moment
        // it goes in, and never otherwise (#292).
        public static bool IsValidTransition(VehiclePartState current, VehiclePartState next, bool steeringRack, bool wheel = false, bool boltless = false,
            MountedVehiclePartKind mountedKind = MountedVehiclePartKind.None, int mountedOptionCount = 0)
        {
            if (mountedKind != MountedVehiclePartKind.None)
                return !steeringRack && !wheel && MountedVehiclePartValues.IsValidTransition(current, next, mountedKind, boltless, mountedOptionCount);
            if (boltless && !steeringRack && !wheel)
            {
                if (current.HasNativeValues || next.HasNativeValues) return false;
                if (!next.Installed) return current.Installed && next.Equals(VehiclePartState.Empty);
                return next.FullyTightened && !current.Installed;
            }
            if (wheel && !steeringRack)
            {
                if ((current.Installed && !current.HasValidWheelValues) || (next.Installed && !next.HasValidWheelValues)) return false;
                if (!next.Installed) return current.Installed && next.Equals(VehiclePartState.Empty);
                if (!current.Installed) return !next.FullyTightened;
                return current.Prefab == next.Prefab && !current.FullyTightened && next.FullyTightened && SameNativeValues(current, next);
            }
            if ((!steeringRack && (current.HasNativeValues || next.HasNativeValues)) ||
                (steeringRack && current.Installed && !current.HasValidSteeringRackValues)) return false;
            if (!next.Installed) return current.Installed && next.Equals(VehiclePartState.Empty);
            if (!current.Installed)
                return !next.FullyTightened && (steeringRack ? next.HasValidSteeringRackValues : !next.HasNativeValues);
            if (current.Prefab != next.Prefab) return false;
            if (current.FullyTightened != next.FullyTightened)
                return !current.FullyTightened && next.FullyTightened && SameNativeValues(current, next);
            if (current.Equals(next)) return false;
            return steeringRack && IsOneRackStep(current, next);
        }
        public static bool IsOneRackStep(VehiclePartState current, VehiclePartState next)
            => TryGetSteeringRackStep(current, next, out _, out _);

        // Native Part_SteeringRack exposes one-step knob operations. Returning the exact
        // operation lets the plugin apply the same native behavior instead of relying on
        // Value1/Value2 setters that intentionally no-op when their knob references are absent.
        // Controls: 0=front-left toe, 1=front-right toe, 2=maximum steer angle, 3=steer speed.
        public static bool TryGetSteeringRackStep(VehiclePartState current, VehiclePartState next,
            out int control, out int direction)
        {
            control = -1;
            direction = 0;
            if (!current.HasValidSteeringRackValues || !next.HasValidSteeringRackValues ||
                current.Prefab != next.Prefab || current.FullyTightened != next.FullyTightened) return false;
            int changes = 0;
            if (current.IntValue1 != next.IntValue1)
            {
                if (Math.Abs((long)current.IntValue1 - next.IntValue1) != 1) return false;
                control = 0; direction = Math.Sign(next.IntValue1 - current.IntValue1); changes++;
            }
            if (current.IntValue2 != next.IntValue2)
            {
                if (Math.Abs((long)current.IntValue2 - next.IntValue2) != 1) return false;
                control = 1; direction = Math.Sign(next.IntValue2 - current.IntValue2); changes++;
            }
            if (current.Value1 != next.Value1)
            {
                if (Math.Abs(current.Value1 - next.Value1) != 1f) return false;
                control = 2; direction = Math.Sign(next.Value1 - current.Value1); changes++;
            }
            if (current.Value2 != next.Value2)
            {
                if (Math.Abs(current.Value2 - next.Value2) != 1f) return false;
                control = 3; direction = Math.Sign(next.Value2 - current.Value2); changes++;
            }
            if (changes == 1) return true;
            control = -1;
            direction = 0;
            return false;
        }
        private static bool SameNativeValues(VehiclePartState a, VehiclePartState b) =>
            a.Value1 == b.Value1 && a.Value2 == b.Value2 && a.IntValue1 == b.IntValue1 && a.IntValue2 == b.IntValue2;
    }
}
