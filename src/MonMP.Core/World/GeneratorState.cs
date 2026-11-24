using System;
using System.IO;

namespace MonMP.Core.World
{
    [Flags]
    public enum GeneratorFlags : byte
    {
        None = 0,
        CombustionRunning = 1,
        KillSwitchEnabled = 2,
        HydroRunning = 4,
        All = CombustionRunning | KillSwitchEnabled | HydroRunning
    }

    // Home power source state. Fuel and hydro accounting stay native on the host; MeterKwh is the
    // host's already-computed Home_HydroCompteur presentation sample, not a guest billing input.
    public readonly struct GeneratorState : IEquatable<GeneratorState>
    {
        public const ushort Adapter = 30;
        public const int Size = 5;
        // A wire-safety ceiling, intentionally far above any stock load combination so large
        // home layouts and future native device counts cannot make a valid host sample unencodable.
        public const float MaxMeterKwh = 1000000f;
        public readonly GeneratorFlags Flags;
        public readonly float MeterKwh;

        public GeneratorState(bool running, bool killSwitchEnabled, bool hydroRunning = false, float meterKwh = 0f)
        {
            Flags = (running ? GeneratorFlags.CombustionRunning : GeneratorFlags.None) |
                    (killSwitchEnabled ? GeneratorFlags.KillSwitchEnabled : GeneratorFlags.None) |
                    (hydroRunning ? GeneratorFlags.HydroRunning : GeneratorFlags.None);
            MeterKwh = meterKwh;
            Validate();
        }

        private GeneratorState(GeneratorFlags flags, float meterKwh) { Flags = flags; MeterKwh = meterKwh; Validate(); }
        public bool Running => (Flags & GeneratorFlags.CombustionRunning) != 0;
        public bool KillSwitchEnabled => (Flags & GeneratorFlags.KillSwitchEnabled) != 0;
        public bool HydroRunning => (Flags & GeneratorFlags.HydroRunning) != 0;

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Flags);
            writer.Write(MeterKwh);
            return stream.ToArray();
        }
        public static GeneratorState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid generator state payload.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var flags = (GeneratorFlags)reader.ReadByte();
            var meterKwh = reader.ReadSingle();
            return new GeneratorState(flags, meterKwh);
        }
        private void Validate()
        {
            if ((Flags & ~GeneratorFlags.All) != 0 || (Running && !KillSwitchEnabled) ||
                float.IsNaN(MeterKwh) || float.IsInfinity(MeterKwh) || MeterKwh < 0f || MeterKwh > MaxMeterKwh)
                throw new InvalidDataException("Invalid generator state.");
        }
        public bool Equals(GeneratorState other) => Flags == other.Flags && MeterKwh.Equals(other.MeterKwh);
        public override bool Equals(object? obj) => obj is GeneratorState other && Equals(other);
        public override int GetHashCode() => ((int)Flags * 397) ^ MeterKwh.GetHashCode();
    }

    public enum GeneratorCommandKind : byte { Start = 1, SetKillSwitch = 2 }

    // Guest controls are idempotent: the kill switch carries its desired state rather than a toggle.
    public readonly struct GeneratorCommand
    {
        public const byte Marker = 0xDA;
        public const int Size = 3;
        public readonly GeneratorCommandKind Kind;
        public readonly bool Enabled;

        public GeneratorCommand(GeneratorCommandKind kind, bool enabled = false)
        {
            if ((kind == GeneratorCommandKind.Start && enabled) ||
                (kind != GeneratorCommandKind.Start && kind != GeneratorCommandKind.SetKillSwitch))
                throw new InvalidDataException("Invalid generator command.");
            Kind = kind; Enabled = enabled;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode() => new[] { Marker, (byte)Kind, (byte)(Enabled ? 1 : 0) };
        public static GeneratorCommand Decode(byte[] payload)
        {
            if (!Is(payload) || payload[2] > 1) throw new InvalidDataException("Invalid generator command payload.");
            var kind = (GeneratorCommandKind)payload[1];
            var enabled = payload[2] == 1;
            if (kind == GeneratorCommandKind.Start && enabled) throw new InvalidDataException("Start command has an invalid value.");
            return new GeneratorCommand(kind, enabled);
        }
    }
}
