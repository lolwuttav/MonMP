using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum GarageKind : byte { Home = 1, Speedway = 2 }

    // One record per motorized garage. Home: Stage is the native build day counter (-1 unbuilt,
    // 5+ finished, clamped to 5), Motion the door animator speed (-0.7 closing, 0 stopped, 0.7 opening)
    // and Position 1 while the door clip is open. Speedway: Motion is the door target (0/1) and
    // Position its travel 0..1. A guest press is a Press command on the record.
    public readonly struct GarageState : IEquatable<GarageState>
    {
        public const ushort Adapter = 22;
        public const byte Press = 0xE1;
        public const float MaxMotion = 1;
        public readonly GarageKind Kind;
        public readonly sbyte Stage;
        public readonly float Motion;
        public readonly float Position;
        public GarageState(GarageKind kind, sbyte stage, float motion, float position)
        { Kind = kind; Stage = stage; Motion = motion; Position = position; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)Kind); w.Write(Stage); w.Write(Motion); w.Write(Position); return stream.ToArray();
        }
        public static GarageState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 10) throw new InvalidDataException("Invalid garage payload.");
            return new GarageState((GarageKind)payload[0], (sbyte)payload[1], BitConverter.ToSingle(payload, 2), BitConverter.ToSingle(payload, 6));
        }
        public static bool IsPress(byte[] payload) => payload != null && payload.Length == 1 && payload[0] == Press;
        private void Validate()
        {
            bool ok = ItemPose.Finite(Motion, MaxMotion) && ItemPose.Finite(Position, 1) && Position >= 0 && Kind switch
            {
                GarageKind.Home => Stage >= -1 && Stage <= 5 && (Position == 0 || Position == 1),
                GarageKind.Speedway => Stage == 0 && (Motion == 0 || Motion == 1),
                _ => false
            };
            if (!ok) throw new InvalidDataException("Invalid garage state.");
        }
        public bool Equals(GarageState other) => Kind == other.Kind && Stage == other.Stage && Math.Abs(Motion - other.Motion) < 0.001f && Math.Abs(Position - other.Position) < 0.02f;
    }
}
