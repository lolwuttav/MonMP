using System;
using System.IO;

namespace MonMP.Core.World
{
    // The host owns the target endpoint; guests animate it through the native button.
    public readonly struct BunkerBathtubState : IEquatable<BunkerBathtubState>
    {
        public const ushort Adapter = 55;
        public const int Size = 1;
        public bool TargetOpen { get; }

        public BunkerBathtubState(bool targetOpen) => TargetOpen = targetOpen;

        public byte[] Encode() => new[] { TargetOpen ? (byte)1 : (byte)0 };

        public static BunkerBathtubState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] > 1)
                throw new InvalidDataException("Invalid bunker bathtub state payload.");
            return new BunkerBathtubState(payload[0] == 1);
        }

        public bool Equals(BunkerBathtubState other) => TargetOpen == other.TargetOpen;
        public override bool Equals(object? obj) => obj is BunkerBathtubState other && Equals(other);
        public override int GetHashCode() => TargetOpen ? 1 : 0;
    }

    public static class BunkerBathtubMessage
    {
        public const byte Marker = 0xD5;
        public const int ToggleCommandSize = 2;
        public static byte[] ToggleCommand() => new[] { Marker, (byte)1 };
        public static bool IsToggleCommand(byte[] payload) => payload != null && payload.Length == ToggleCommandSize &&
            payload[0] == Marker && payload[1] == 1;
    }
}
