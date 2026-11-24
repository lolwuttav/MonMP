using System;
using System.IO;

namespace MonMP.Core.World
{
    // Native BuggyBuild stores six installed packs in this order: tubes, steering, wheels,
    // drivetrain, seat/plastics, engine. The last bit makes the buggy drivable natively.
    public readonly struct BuggyBuildState : IEquatable<BuggyBuildState>
    {
        public const ushort Adapter = 31;
        public const byte CompleteMask = 0x3f;
        public readonly byte Installed;

        public BuggyBuildState(byte installed)
        {
            if ((installed & ~CompleteMask) != 0) throw new InvalidDataException("Invalid buggy pack mask.");
            Installed = installed;
        }

        public bool Has(int pack) => pack >= 0 && pack < 6 && (Installed & (1 << pack)) != 0;
        public bool Complete => Installed == CompleteMask;
        public byte[] Encode() => new[] { Installed };
        public static BuggyBuildState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 1) throw new InvalidDataException("Invalid buggy build payload.");
            return new BuggyBuildState(payload[0]);
        }
        public bool Equals(BuggyBuildState other) => Installed == other.Installed;
        public override bool Equals(object? obj) => obj is BuggyBuildState other && Equals(other);
        public override int GetHashCode() => Installed;
    }
}
