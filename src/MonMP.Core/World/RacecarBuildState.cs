using System;
using System.IO;

namespace MonMP.Core.World
{
    // Racecar packs use the native Event_RacecarPack.IntValue1 order. Pack collection and
    // Speedway rewards stay native on the host; this compact record shares the build result.
    public readonly struct RacecarBuildState : IEquatable<RacecarBuildState>
    {
        public const ushort Adapter = 78;
        public const byte CompleteMask = 0x07;
        public static readonly ulong Entity = StaticId.Hash("shared-racecar-build/v1");
        public readonly byte Installed;

        public RacecarBuildState(byte installed)
        {
            if ((installed & ~CompleteMask) != 0) throw new InvalidDataException("Invalid racecar pack mask.");
            Installed = installed;
        }

        public bool Has(int pack) => pack >= 0 && pack < 3 && (Installed & (1 << pack)) != 0;
        public bool Complete => Installed == CompleteMask;
        public RacecarBuildState Install(int pack)
        {
            if (pack < 0 || pack >= 3) throw new ArgumentOutOfRangeException(nameof(pack));
            return new RacecarBuildState((byte)(Installed | (1 << pack)));
        }
        public byte[] Encode() => new[] { Installed };
        public static RacecarBuildState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 1) throw new InvalidDataException("Invalid racecar build payload.");
            return new RacecarBuildState(payload[0]);
        }
        public static bool AllowNativePackMutation(bool sessionRunning, bool host, bool saveBlocked,
            RacecarBuildState current, int pack)
        {
            if (!sessionRunning) return true;
            return host && !saveBlocked && pack >= 0 && pack < 3 && !current.Has(pack);
        }
        public static bool AllowNativeRewardClaim(bool sessionRunning, bool host) => !sessionRunning || host;
        public bool Equals(RacecarBuildState other) => Installed == other.Installed;
        public override bool Equals(object? obj) => obj is RacecarBuildState other && Equals(other);
        public override int GetHashCode() => Installed;
    }
}
