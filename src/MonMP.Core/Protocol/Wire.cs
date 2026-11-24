using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MonMP.Core.Protocol
{
    public enum MessageKind : byte { Hello = 1, Welcome, PeerJoined, PeerLeft, Pose, Reject, SnapshotBegin, SnapshotRecord, SnapshotEnd, WorldDelta, WorldCommand, ResyncRequest, WorldCheckpoint, SceneReady, SceneAccepted, SnapshotCaughtUp, SnapshotAcknowledged, WorldReply, Chat, CdMedia, CheatState, CheatAction, SocialPing, CheatFeedback, SessionSettings }

    // Explicit little-endian, bounded messages. No runtime type names or object deserialization.
    public static class Wire
    {
        public const ushort Version = 93;
        public const int MaxPacketBytes = 1024;
        public const int MaxNameBytes = 48;
        private const uint Magic = 0x504D4F4D;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(MessageKind kind, Guid epoch, Action<BinaryWriter> body)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((byte)kind);
            Span<byte> epochBytes = stackalloc byte[16];
            epoch.TryWriteBytes(epochBytes);
            // Unity Mono's BinaryWriter.Write(ReadOnlySpan<byte>) calls ToArray.
            // Its scalar writes retain the exact bytes without that hidden copy.
            writer.Write(BinaryPrimitives.ReadUInt64LittleEndian(epochBytes.Slice(0, 8)));
            writer.Write(BinaryPrimitives.ReadUInt64LittleEndian(epochBytes.Slice(8, 8)));
            body(writer);
            if (stream.Length > MaxPacketBytes) throw new InvalidDataException("Packet exceeds size limit.");
            return stream.ToArray();
        }

        // Classification only; Decode and the payload reader still validate the message.
        // Used for a separately bounded, authenticated media lane, never before Hello.
        public static bool HasEnvelope(byte[] bytes, MessageKind kind, Guid epoch) =>
            bytes != null && bytes.Length >= 23 && bytes.Length <= MaxPacketBytes &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == Magic &&
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)) == Version &&
            bytes[6] == (byte)kind && new Guid(bytes.AsSpan(7, 16)) == epoch;

        public static Packet Decode(byte[] bytes)
        {
            if (bytes.Length < 23 || bytes.Length > MaxPacketBytes) throw new InvalidDataException("Invalid packet size.");
            var packet = new Packet(bytes);
            try
            {
                if (packet.Reader.ReadUInt32() != Magic || packet.Reader.ReadUInt16() != Version)
                    throw new InvalidDataException("Protocol mismatch.");
                byte kind = packet.Reader.ReadByte();
                // The wire enum is contiguous; avoid reflection and enum boxing on
                // every received pose, command and snapshot record.
                if (kind < (byte)MessageKind.Hello || kind > (byte)MessageKind.SessionSettings) throw new InvalidDataException("Unknown packet type.");
                packet.Kind = (MessageKind)kind;
                packet.Epoch = new Guid(bytes.AsSpan(7, 16));
                packet.Reader.BaseStream.Position = 23;
                return packet;
            }
            catch { packet.Dispose(); throw; }
        }

        public static void WriteText(BinaryWriter writer, string value, int maxBytes)
        {
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length > maxBytes) throw new InvalidDataException("Text exceeds size limit.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        public static string ReadText(BinaryReader reader, int maxBytes)
        {
            int count = reader.ReadUInt16();
            if (count > maxBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid text length.");
            return Utf8.GetString(reader.ReadBytes(count));
        }

        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Utf8.GetByteCount(name) > MaxNameBytes)
                throw new InvalidDataException("Display name must contain 1 to 48 UTF-8 bytes.");
            foreach (char c in name)
                if (char.IsControl(c) || c == '<' || c == '>') throw new InvalidDataException("Invalid display name.");
        }

        // Half-range comparison continues to work when a 32-bit sequence wraps.
        public static bool IsNewer(uint incoming, uint previous) => unchecked((int)(incoming - previous)) > 0;
    }

    public sealed class Packet : IDisposable
    {
        public MessageKind Kind { get; internal set; }
        public Guid Epoch { get; internal set; }
        public BinaryReader Reader { get; }
        internal Packet(byte[] bytes) => Reader = new BinaryReader(new MemoryStream(bytes, false));
        public void Complete()
        {
            if (Reader.BaseStream.Position != Reader.BaseStream.Length) throw new InvalidDataException("Trailing packet data.");
        }
        public void Dispose() => Reader.Dispose();
    }
}
