using System;
using System.IO;

namespace MonMP.Core.World
{
    [Flags]
    public enum VehicleEffectFlags : byte { None = 0, Horn = 1, RevLimiter = 2, CoolantBoiling = 4 }

    // The driver's presentation-only inputs that a kinematic copy cannot derive: the held horn, the
    // engine's rev limiter (exhaust flashes), the coolant boiling (steam and hiss, Astra R14) and each wheel's normalized lateral / longitudinal slip
    // (0..1 in 1/255 steps, wheel-spin order), which drive tire smoke, skid sounds and skidmarks.
    public readonly struct VehicleEffects : IEquatable<VehicleEffects>
    {
        public readonly VehicleEffectFlags Flags;
        private readonly uint lateral, longitudinal;
        // The engine block's heat (NWH CoolantModule.EngineHeatPercent) in 31 steps, in the flag byte's
        // five spare bits: observers' gauges read it, and the next driver's engine continues from it
        // instead of its own stale temperature (Astra R14).
        private readonly byte heat, radiatorHeat;
        public const int HeatSteps = 31;

        public VehicleEffects(VehicleEffectFlags flags, float lat0, float lat1, float lat2, float lat3, float lon0, float lon1, float lon2, float lon3, float engineHeat = 0, float radiatorHeat = 0)
        {
            Flags = flags;
            lateral = Pack(lat0, lat1, lat2, lat3);
            longitudinal = Pack(lon0, lon1, lon2, lon3);
            heat = Heat(engineHeat);
            this.radiatorHeat = Heat(radiatorHeat);
        }
        private VehicleEffects(VehicleEffectFlags flags, uint lat, uint lon, byte heat, byte radiatorHeat = 0)
        { Flags = flags; lateral = lat; longitudinal = lon; this.heat = heat; this.radiatorHeat = radiatorHeat; }

        private static byte Heat(float value) => float.IsNaN(value) || value <= 0 ? (byte)0 : value >= 1
            ? (byte)HeatSteps : (byte)Math.Round(value * HeatSteps, MidpointRounding.AwayFromZero);

        public float EngineHeat => heat / (float)HeatSteps;
        // Independent native coolant reservoir. Its five bits share the lighting control byte's
        // spare high bits in VehicleState, keeping a truck driver command within 128 bytes.
        public float RadiatorHeat => radiatorHeat / (float)HeatSteps;
        internal byte RadiatorHeatStep => radiatorHeat;
        internal VehicleEffects WithRadiatorHeat(byte value) => new VehicleEffects(Flags, lateral, longitudinal, heat, value);
        public bool Horn => (Flags & VehicleEffectFlags.Horn) != 0;
        public bool RevLimiter => (Flags & VehicleEffectFlags.RevLimiter) != 0;
        public bool CoolantBoiling => (Flags & VehicleEffectFlags.CoolantBoiling) != 0;
        public float Lateral(int wheel) => Unpack(lateral, wheel);
        public float Longitudinal(int wheel) => Unpack(longitudinal, wheel);

        private static uint Pack(float a, float b, float c, float d) => Byte(a) | (uint)Byte(b) << 8 | (uint)Byte(c) << 16 | (uint)Byte(d) << 24;
        private static byte Byte(float value)
        {
            if (float.IsNaN(value) || value <= 0) return 0;
            return value >= 1 ? (byte)255 : (byte)Math.Round(value * 255, MidpointRounding.AwayFromZero);
        }
        private static float Unpack(uint packed, int wheel)
        {
            if (wheel < 0 || wheel > 3) throw new ArgumentOutOfRangeException(nameof(wheel));
            return (byte)(packed >> (wheel * 8)) / 255f;
        }

        internal void Write(BinaryWriter writer)
        {
            if (((byte)Flags & ~7) != 0 || heat > HeatSteps) throw new InvalidDataException("Invalid vehicle effect flags.");
            writer.Write((byte)((byte)Flags | heat << 3)); writer.Write(lateral); writer.Write(longitudinal);
        }
        internal static VehicleEffects Read(BinaryReader reader)
        {
            byte flags = reader.ReadByte();
            return new VehicleEffects((VehicleEffectFlags)(flags & 7), reader.ReadUInt32(), reader.ReadUInt32(), (byte)(flags >> 3));
        }

        public bool Equals(VehicleEffects other) => Flags == other.Flags && lateral == other.lateral && longitudinal == other.longitudinal && heat == other.heat && radiatorHeat == other.radiatorHeat;
        public override bool Equals(object? obj) => obj is VehicleEffects other && Equals(other);
        public override int GetHashCode() => ((((int)Flags * 397 ^ (int)lateral) * 397 ^ (int)longitudinal) * 397 ^ heat) * 397 ^ radiatorHeat;
    }
}
