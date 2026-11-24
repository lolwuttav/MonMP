using System;
using System.IO;

namespace MonMP.Core.World
{
    // Bounded representation of the fields stored by the game's PaintPannelData.
    // A paint adapter can put this in a stable panel entity for initial snapshots,
    // deltas, and late-join repair without serializing Unity objects or materials.
    public sealed class PaintPanelState
    {
        public const byte Marker = 0xD5;
        public const byte Version = 1;
        public const int MaxDepth = 3;
        // Vehicle_PaintablePanel.Start reads the native material's raw _BumpScale;
        // the game clamps only when later applying save data or repairing a dent.
        // Keep measured native values such as 8.4 intact while bounding the wire value.
        public const float MaxNativeDentScale = 16f;
        private const int LevelSize = 1 + 6 * sizeof(float);
        public const int MaxPayloadSize = 2 + MaxDepth * LevelSize;

        public readonly bool IsRusted;
        public readonly bool ForceRust;
        public readonly float Red, Green, Blue;
        public readonly float DentScale, Smoothness, Metalness;
        public readonly PaintPanelState? Associated;

        public PaintPanelState(bool isRusted, bool forceRust, float red, float green, float blue,
            float dentScale, float smoothness, float metalness, PaintPanelState? associated = null)
        {
            IsRusted = isRusted;
            ForceRust = forceRust;
            Red = red; Green = green; Blue = blue;
            DentScale = dentScale; Smoothness = smoothness; Metalness = metalness;
            Associated = associated;
            Validate(0);
        }

        public byte[] Encode()
        {
            Validate(0);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Version);
            WriteLevel(writer, 0);
            if (stream.Length > MaxPayloadSize) throw new InvalidDataException("Paint panel state is too large.");
            return stream.ToArray();
        }

        // Instances and their associated graphs are immutable and validated on construction.
        // Compare the bits BinaryWriter writes, including the distinction between +0 and -0.
        public bool SameState(PaintPanelState? other) => other != null &&
            IsRusted == other.IsRusted && ForceRust == other.ForceRust &&
            SameFloat(Red, other.Red) && SameFloat(Green, other.Green) && SameFloat(Blue, other.Blue) &&
            SameFloat(DentScale, other.DentScale) && SameFloat(Smoothness, other.Smoothness) &&
            SameFloat(Metalness, other.Metalness) &&
            (Associated == null ? other.Associated == null : Associated.SameState(other.Associated));

        private static bool SameFloat(float a, float b) =>
            BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        public static PaintPanelState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 2 + LevelSize || payload.Length > MaxPayloadSize)
                throw new InvalidDataException("Invalid paint panel state size.");
            try
            {
                using var stream = new MemoryStream(payload, false);
                using var reader = new BinaryReader(stream);
                if (reader.ReadByte() != Marker || reader.ReadByte() != Version)
                    throw new InvalidDataException("Unsupported paint panel state format.");
                var state = ReadLevel(reader, stream, 0);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing paint panel state data.");
                return state;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Truncated paint panel state.", ex);
            }
        }

        private void WriteLevel(BinaryWriter writer, int depth)
        {
            if (depth >= MaxDepth) throw new InvalidDataException("Paint panel association is too deep.");
            byte flags = (byte)((IsRusted ? 1 : 0) | (ForceRust ? 2 : 0) | (Associated != null ? 4 : 0));
            writer.Write(flags);
            writer.Write(Red); writer.Write(Green); writer.Write(Blue);
            writer.Write(DentScale); writer.Write(Smoothness); writer.Write(Metalness);
            Associated?.WriteLevel(writer, depth + 1);
        }

        private static PaintPanelState ReadLevel(BinaryReader reader, MemoryStream stream, int depth)
        {
            if (depth >= MaxDepth || stream.Length - stream.Position < LevelSize)
                throw new InvalidDataException("Invalid paint panel association depth or size.");
            byte flags = reader.ReadByte();
            if ((flags & ~7) != 0) throw new InvalidDataException("Invalid paint panel flags.");
            bool hasAssociated = (flags & 4) != 0;
            var state = new PaintPanelState((flags & 1) != 0, (flags & 2) != 0,
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!hasAssociated) return state;
            return new PaintPanelState(state.IsRusted, state.ForceRust, state.Red, state.Green, state.Blue,
                state.DentScale, state.Smoothness, state.Metalness, ReadLevel(reader, stream, depth + 1));
        }

        private void Validate(int depth)
        {
            if (depth >= MaxDepth || !Unit(Red) || !Unit(Green) || !Unit(Blue) ||
                !Bounded(DentScale, MaxNativeDentScale) || !Unit(Smoothness) || !Unit(Metalness))
                throw new InvalidDataException("Invalid paint panel values.");
            Associated?.Validate(depth + 1);
        }

        private static bool Unit(float value) => Bounded(value, 1f);
        private static bool Bounded(float value, float max) => ItemPose.FiniteValue(value, max) && value >= 0f;
    }
}
