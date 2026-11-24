using System;
using System.IO;

namespace MonMP.Core.World
{
    // One bolt hole on a Konig part socket. Percent is quantized to whole points; holes are fixed
    // per socket (present whether or not the socket's part is installed) so bindings never change.
    public readonly struct BoltHoleState : IEquatable<BoltHoleState>
    {
        public const ushort Adapter = 20;
        public const int PayloadLength = 3;
        public const byte MaximumColor = 15;
        public readonly bool Installed;
        public readonly byte Percent;
        public readonly byte Color;
        public BoltHoleState(bool installed, byte percent, byte color = 0)
        {
            if (percent > 100 || color > MaximumColor || (!installed && (percent != 0 || color != 0)))
                throw new InvalidDataException("Invalid bolt hole state.");
            Installed = installed; Percent = percent; Color = color;
        }
        public byte[] Encode() => new[] { (byte)(Installed ? 1 : 0), Percent, Color };
        public static BoltHoleState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != PayloadLength || payload[0] > 1) throw new InvalidDataException("Invalid bolt hole payload.");
            return new BoltHoleState(payload[0] == 1, payload[1], payload[2]);
        }
        public bool Equals(BoltHoleState other) => Installed == other.Installed && Percent == other.Percent && Color == other.Color;
    }
}
