using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MonMP.Core.World
{
    // Native Vehicle_PartsManager saves this value as text. Keep the scene record textual so
    // custom labels already present in a save survive snapshot and save/reload unchanged.
    public readonly struct VehiclePlateState : IEquatable<VehiclePlateState>
    {
        public const ushort Adapter = 53;
        public const int MaxTextLength = 7;
        private const int MaxUtf8Bytes = MaxTextLength * 4;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public readonly string Text;

        public VehiclePlateState(string text)
        {
            if (text == null || text.Length > MaxTextLength)
                throw new InvalidDataException("Invalid vehicle plate text.");
            try
            {
                if (Utf8.GetByteCount(text) > MaxUtf8Bytes) throw new InvalidDataException("Invalid vehicle plate text.");
            }
            catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid vehicle plate text.", ex); }
            Text = text;
        }

        public byte[] Encode()
        {
            byte[] bytes;
            try { bytes = Utf8.GetBytes(Text); }
            catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid vehicle plate text.", ex); }
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)bytes.Length); writer.Write(bytes);
            return stream.ToArray();
        }

        public static VehiclePlateState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 1 || payload[0] > MaxUtf8Bytes || payload.Length != payload[0] + 1)
                throw new InvalidDataException("Invalid vehicle plate payload.");
            string text;
            try { text = Utf8.GetString(payload, 1, payload[0]); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid vehicle plate payload.", ex); }
            if (text.Length > MaxTextLength) throw new InvalidDataException("Invalid vehicle plate payload.");
            return new VehiclePlateState(text);
        }

        public bool Equals(VehiclePlateState other) => string.Equals(Text, other.Text, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is VehiclePlateState other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);
        public static bool operator ==(VehiclePlateState left, VehiclePlateState right) => left.Equals(right);
        public static bool operator !=(VehiclePlateState left, VehiclePlateState right) => !left.Equals(right);
    }

    public static class VehiclePlateCycle
    {
        public static readonly System.Collections.Generic.IReadOnlyList<string> Texts = Array.AsReadOnly(new[]
        {
            "BAZOU", "SANTA", "123 ABC", "GOAT", "BRAAAAP", "SAPOUUD", "JAIDOUA", "KONIG", "MACHINE", "420-420", "696969"
        });

        // Matches Vehicle_Plate.GetNextText, including its reset-to-first behavior for custom labels.
        public static string Next(string current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            int index = -1;
            for (int i = 0; i < Texts.Count; i++)
                if (string.Equals(Texts[i], current, StringComparison.Ordinal)) { index = i; break; }
            return Texts[index < 0 || index + 1 == Texts.Count ? 0 : index + 1];
        }

        public static bool IsNext(string current, string proposed) =>
            current != null && proposed != null && string.Equals(Next(current), proposed, StringComparison.Ordinal);
    }
}
