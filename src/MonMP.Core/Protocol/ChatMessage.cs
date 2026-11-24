using System;
using System.IO;
using System.Text;

namespace MonMP.Core.Protocol
{
    public readonly struct ChatMessage
    {
        public const int MaxCharacters = 120, MaxUtf8Bytes = MaxCharacters * 4;
        public const float CaptionSeconds = 8;
        public uint PeerId { get; }
        public string Text { get; }
        public ChatMessage(uint peerId, string text)
        {
            Validate(text); PeerId = peerId; Text = text;
        }
        public static void Validate(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters)
                throw new InvalidDataException("Chat must contain 1 to 120 characters.");
            foreach (char c in text)
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                    throw new InvalidDataException("Chat must be one line without control characters.");
            // Reject unpaired UTF-16 surrogates before they can be replaced on the wire.
            if (new UTF8Encoding(false, true).GetByteCount(text) > MaxUtf8Bytes)
                throw new InvalidDataException("Chat exceeds its UTF-8 limit.");
        }
        public void Write(BinaryWriter writer) { writer.Write(PeerId); Wire.WriteText(writer, Text, MaxUtf8Bytes); }
        public static ChatMessage Read(BinaryReader reader) => new ChatMessage(reader.ReadUInt32(), Wire.ReadText(reader, MaxUtf8Bytes));
    }

    // One message per second, with three immediately available after connecting or a quiet period.
    public sealed class ChatRateLimit
    {
        private double tokens = 3, updated;
        public bool Take(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < updated) return false;
            tokens = Math.Min(3, tokens + seconds - updated); updated = seconds;
            if (tokens < 1) return false;
            tokens--; return true;
        }
    }
}
