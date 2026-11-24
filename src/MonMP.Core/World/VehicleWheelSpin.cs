using System;
using System.IO;

namespace MonMP.Core.World
{
    // Four stock wheels are transmitted in canonical transform-path order as signed bytes.
    // Values are angular velocity in radians per second, matching NWH Wheel.angularVelocity.
    public readonly struct VehicleWheelSpin : IEquatable<VehicleWheelSpin>
    {
        public const float AngularVelocityStep = 4f;
        public const float MaximumAngularVelocity = 127f * AngularVelocityStep;
        public readonly float Wheel0, Wheel1, Wheel2, Wheel3;

        public VehicleWheelSpin(float wheel0, float wheel1, float wheel2, float wheel3)
        { Wheel0 = wheel0; Wheel1 = wheel1; Wheel2 = wheel2; Wheel3 = wheel3; }

        public float this[int index] => index switch
        {
            0 => Wheel0,
            1 => Wheel1,
            2 => Wheel2,
            3 => Wheel3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public float MaxAbsAngularVelocity => Math.Max(Math.Max(Math.Abs(Wheel0), Math.Abs(Wheel1)), Math.Max(Math.Abs(Wheel2), Math.Abs(Wheel3)));

        internal void Validate()
        {
            if (!Valid(Wheel0) || !Valid(Wheel1) || !Valid(Wheel2) || !Valid(Wheel3))
                throw new InvalidDataException("Invalid vehicle wheel angular velocity.");
        }

        private static bool Valid(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaximumAngularVelocity;
        private static void WriteValue(BinaryWriter writer, float value) => writer.Write((sbyte)Math.Round(value / AngularVelocityStep, MidpointRounding.AwayFromZero));
        private static float ReadValue(BinaryReader reader) => reader.ReadSByte() * AngularVelocityStep;
        internal void Write(BinaryWriter writer) { WriteValue(writer, Wheel0); WriteValue(writer, Wheel1); WriteValue(writer, Wheel2); WriteValue(writer, Wheel3); }
        internal static VehicleWheelSpin Read(BinaryReader reader) => new VehicleWheelSpin(ReadValue(reader), ReadValue(reader), ReadValue(reader), ReadValue(reader));

        public bool Equals(VehicleWheelSpin other) => Wheel0.Equals(other.Wheel0) && Wheel1.Equals(other.Wheel1) &&
            Wheel2.Equals(other.Wheel2) && Wheel3.Equals(other.Wheel3);
        public override bool Equals(object? obj) => obj is VehicleWheelSpin other && Equals(other);
        public override int GetHashCode() => (((Wheel0.GetHashCode() * 397) ^ Wheel1.GetHashCode()) * 397 ^ Wheel2.GetHashCode()) * 397 ^ Wheel3.GetHashCode();
    }

    // The four wheels' roll angles (NWH Wheel.rotationAngle) in the same order, six bits each
    // (5.6 degree steps, a few millimetres at a lug nut), packed in three bytes to keep a truck's
    // command within the 128-byte limit. Spin alone let each peer's wheels settle at their own angle, so a wheel's
    // bolt holes sat elsewhere on each screen (#184).
    public readonly struct VehicleWheelAngles : IEquatable<VehicleWheelAngles>
    {
        public const float Step = 360f / 64f;
        public readonly byte Angle0, Angle1, Angle2, Angle3;

        public VehicleWheelAngles(float angle0, float angle1, float angle2, float angle3)
        { Angle0 = Quantize(angle0); Angle1 = Quantize(angle1); Angle2 = Quantize(angle2); Angle3 = Quantize(angle3); }
        private VehicleWheelAngles(byte a0, byte a1, byte a2, byte a3) { Angle0 = a0; Angle1 = a1; Angle2 = a2; Angle3 = a3; }

        public float this[int index] => (index switch
        {
            0 => Angle0,
            1 => Angle1,
            2 => Angle2,
            3 => Angle3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        }) * Step;

        private static byte Quantize(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0;
            double turns = degrees / 360.0; turns -= Math.Floor(turns);
            return (byte)((int)Math.Round(turns * 64.0, MidpointRounding.AwayFromZero) & 63);
        }
        internal void Write(BinaryWriter writer)
        {
            int packed = Angle0 | Angle1 << 6 | Angle2 << 12 | Angle3 << 18;
            writer.Write((byte)packed); writer.Write((byte)(packed >> 8)); writer.Write((byte)(packed >> 16));
        }
        internal static VehicleWheelAngles Read(BinaryReader reader)
        {
            int packed = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
            return new VehicleWheelAngles((byte)(packed & 63), (byte)(packed >> 6 & 63), (byte)(packed >> 12 & 63), (byte)(packed >> 18 & 63));
        }

        public bool Equals(VehicleWheelAngles other) => Angle0 == other.Angle0 && Angle1 == other.Angle1 && Angle2 == other.Angle2 && Angle3 == other.Angle3;
        public override bool Equals(object? obj) => obj is VehicleWheelAngles other && Equals(other);
        public override int GetHashCode() => Angle0 | Angle1 << 8 | Angle2 << 16 | Angle3 << 24;
    }
}
