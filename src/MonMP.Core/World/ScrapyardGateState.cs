using System;
using System.IO;

namespace MonMP.Core.World
{
    // Presentation for the native scrapyard entrance. Progression remains owned by ProgressState.
    public readonly struct ScrapyardGateState : IEquatable<ScrapyardGateState>
    {
        public const ushort Adapter = 34;
        public const int Size = 33;

        public readonly float LeftX, LeftY, LeftZ, LeftW;
        public readonly float RightX, RightY, RightZ, RightW;
        public readonly bool LockedCollider, NormalCollider, Pin, Dust;

        public ScrapyardGateState(float leftX, float leftY, float leftZ, float leftW,
            float rightX, float rightY, float rightZ, float rightW,
            bool lockedCollider, bool normalCollider, bool pin, bool dust)
        {
            LeftX = leftX; LeftY = leftY; LeftZ = leftZ; LeftW = leftW;
            RightX = rightX; RightY = rightY; RightZ = rightZ; RightW = rightW;
            LockedCollider = lockedCollider; NormalCollider = normalCollider; Pin = pin; Dust = dust;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using (var stream = new MemoryStream(Size))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(LeftX); writer.Write(LeftY); writer.Write(LeftZ); writer.Write(LeftW);
                writer.Write(RightX); writer.Write(RightY); writer.Write(RightZ); writer.Write(RightW);
                writer.Write((byte)((LockedCollider ? 1 : 0) | (NormalCollider ? 2 : 0) | (Pin ? 4 : 0) | (Dust ? 8 : 0)));
                return stream.ToArray();
            }
        }

        public static ScrapyardGateState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || (payload[Size - 1] & 0xF0) != 0)
                throw new InvalidDataException("Invalid scrapyard gate state payload.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream))
            {
                float lx = reader.ReadSingle(), ly = reader.ReadSingle(), lz = reader.ReadSingle(), lw = reader.ReadSingle();
                float rx = reader.ReadSingle(), ry = reader.ReadSingle(), rz = reader.ReadSingle(), rw = reader.ReadSingle();
                byte flags = reader.ReadByte();
                return new ScrapyardGateState(lx, ly, lz, lw, rx, ry, rz, rw,
                    (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
            }
        }

        private void Validate()
        {
            if (!Finite(LeftX) || !Finite(LeftY) || !Finite(LeftZ) || !Finite(LeftW) ||
                !Finite(RightX) || !Finite(RightY) || !Finite(RightZ) || !Finite(RightW))
                throw new InvalidDataException("Scrapyard gate rotation contains a non-finite value.");
            float leftLength = LeftX * LeftX + LeftY * LeftY + LeftZ * LeftZ + LeftW * LeftW;
            float rightLength = RightX * RightX + RightY * RightY + RightZ * RightZ + RightW * RightW;
            if (leftLength < 0.81f || leftLength > 1.21f || rightLength < 0.81f || rightLength > 1.21f)
                throw new InvalidDataException("Scrapyard gate rotation is not normalized.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public bool Equals(ScrapyardGateState other) => LeftX == other.LeftX && LeftY == other.LeftY && LeftZ == other.LeftZ && LeftW == other.LeftW &&
            RightX == other.RightX && RightY == other.RightY && RightZ == other.RightZ && RightW == other.RightW &&
            LockedCollider == other.LockedCollider && NormalCollider == other.NormalCollider && Pin == other.Pin && Dust == other.Dust;
        public override bool Equals(object? obj) => obj is ScrapyardGateState other && Equals(other);
        public override int GetHashCode() => LeftX.GetHashCode() ^ LeftY.GetHashCode() ^ LeftZ.GetHashCode() ^ LeftW.GetHashCode() ^
            RightX.GetHashCode() ^ RightY.GetHashCode() ^ RightZ.GetHashCode() ^ RightW.GetHashCode() ^
            (LockedCollider ? 1 : 0) ^ (NormalCollider ? 2 : 0) ^ (Pin ? 4 : 0) ^ (Dust ? 8 : 0);
    }

    public enum ScrapyardGateReply : byte { CriminalRecord = 1, Friendship = 2 }

    public static class ScrapyardGateMessage
    {
        public const byte Marker = 0xD4;
        public const int CommandSize = 2;
        public const int ReplySize = 3;
        public static byte[] OpenCommand() => new[] { Marker, (byte)1 };
        public static bool IsOpenCommand(byte[] payload) => payload != null && payload.Length == CommandSize && payload[0] == Marker && payload[1] == 1;
        public static byte[] EncodeReply(ScrapyardGateReply reason) => new[] { Marker, (byte)2, (byte)reason };
        public static ScrapyardGateReply DecodeReply(byte[] payload)
        {
            if (payload == null || payload.Length != ReplySize || payload[0] != Marker || payload[1] != 2 ||
                (payload[2] != (byte)ScrapyardGateReply.CriminalRecord && payload[2] != (byte)ScrapyardGateReply.Friendship))
                throw new InvalidDataException("Invalid scrapyard gate reply.");
            return (ScrapyardGateReply)payload[2];
        }
    }
}
