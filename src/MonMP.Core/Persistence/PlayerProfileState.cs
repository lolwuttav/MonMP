using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.Persistence
{
    public enum PersonalNeed { MapleSyrup, Poutine, Energy, Anxiety, Thc, Drunkness, StomachAlcohol }
    public enum PersonalCounter { DeathDebuff, HangoverDebuff, SinceDrink, SinceFood, SinceSleep, VomitCooldown, EnergyDrink }

    // Build-bound native personal state. World cash, progression and clock are deliberately separate.
    public sealed class PlayerProfileState
    {
        public const string SectionKey = "personal-state";
        public const ushort Version = 2;
        public const ushort LegacyVersion = 1;
        public const int StatCount = 21;
        public const int EncodedBytes = 147;
        public const int LegacyEncodedBytes = 143;
        // A version 1 profile never recorded joints: leave the player's current count alone.
        public const int UnknownWeedJoints = -1;
        public IReadOnlyList<float> Needs { get; }
        public IReadOnlyList<int> Counters { get; }
        public IReadOnlyList<float> Statistics { get; }
        public byte Shirt { get; }
        public int WeedJoints { get; }
        public PlayerProfileState(IEnumerable<float> needs, IEnumerable<int> counters, IEnumerable<float> statistics, int shirt, int weedJoints = UnknownWeedJoints)
        {
            var n = needs.ToArray(); var c = counters.ToArray(); var s = statistics.ToArray();
            // Native ticking can undershoot zero (alcohol), and SkipNight can overshoot one.
            if (n.Length != 7 || n.Any(v => !Finite(v) || v < -1 || v > 8) || c.Length != 7 || c.Any(v => v < 0) ||
                s.Length != StatCount || s.Any(v => !Finite(v) || v < 0) || shirt < 0 || shirt > 8 || weedJoints < UnknownWeedJoints)
                throw new InvalidDataException("Invalid personal profile state.");
            Needs = Array.AsReadOnly(n); Counters = Array.AsReadOnly(c); Statistics = Array.AsReadOnly(s); Shirt = (byte)shirt; WeedJoints = weedJoints;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Version); writer.Write(Shirt);
            foreach (float value in Needs) writer.Write(value);
            foreach (int value in Counters) writer.Write(value);
            foreach (float value in Statistics) writer.Write(value);
            writer.Write(WeedJoints);
            return stream.ToArray();
        }
        public SidecarSection ToSection() => new SidecarSection(Version, Encode());
        public static PlayerProfileState FromSection(SidecarSection section)
        {
            if (section.Version != Version && section.Version != LegacyVersion) throw new InvalidDataException("Unsupported personal profile section.");
            return Decode(section.Payload);
        }
        public static PlayerProfileState Decode(byte[] bytes)
        {
            if (bytes == null || (bytes.Length != EncodedBytes && bytes.Length != LegacyEncodedBytes)) throw new InvalidDataException("Invalid personal profile length.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != (bytes.Length == EncodedBytes ? Version : LegacyVersion)) throw new InvalidDataException("Unsupported personal profile version.");
            int shirt = reader.ReadByte(); var needs = new float[7]; var counters = new int[7]; var stats = new float[StatCount];
            for (int i = 0; i < needs.Length; i++) needs[i] = reader.ReadSingle();
            for (int i = 0; i < counters.Length; i++) counters[i] = reader.ReadInt32();
            for (int i = 0; i < stats.Length; i++) stats[i] = reader.ReadSingle();
            int joints = bytes.Length == EncodedBytes ? reader.ReadInt32() : UnknownWeedJoints;
            return new PlayerProfileState(needs, counters, stats, shirt, joints);
        }
    }
}
