using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    // Application receipts correlate a local visual preview with the exact host decision.
    // They carry the state/revision because replies can precede queued world deltas.
    public readonly struct ClientInteractionRequest
    {
        private const byte Tag = 0xE1;
        public readonly ulong Token;
        public readonly byte[] Payload;
        public ClientInteractionRequest(ulong token, byte[] payload) { Token = token; Payload = payload; }
        public static bool Is(byte[] payload) => payload != null && payload.Length >= 9 && payload[0] == Tag;
        public byte[] Encode()
        {
            if (Token == 0 || Payload == null || Payload.Length > 119) throw new InvalidDataException("Invalid interaction request.");
            var bytes = new byte[9 + Payload.Length]; bytes[0] = Tag;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), Token); Payload.CopyTo(bytes, 9); return bytes;
        }
        public static ClientInteractionRequest Decode(byte[] bytes)
        {
            if (!Is(bytes) || bytes.Length > 128) throw new InvalidDataException("Invalid interaction request.");
            ulong token = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1));
            if (token == 0) throw new InvalidDataException("Invalid interaction request token.");
            return new ClientInteractionRequest(token, bytes.AsSpan(9).ToArray());
        }
    }

    public enum ClientInteractionStatus : byte { Accepted, Refused, Locked, Stale }

    public readonly struct ClientInteractionReceipt
    {
        private const byte Tag = 0xE2;
        public readonly ulong Token;
        public readonly ClientInteractionStatus Status;
        public readonly uint Revision;
        public readonly byte[] State;
        public ClientInteractionReceipt(ulong token, ClientInteractionStatus status, uint revision, byte[] state)
        { Token = token; Status = status; Revision = revision; State = state; }
        public static bool Is(byte[] bytes) => bytes != null && bytes.Length >= 14 && bytes[0] == Tag;
        public byte[] Encode()
        {
            if (Token == 0 || (byte)Status > (byte)ClientInteractionStatus.Stale || Revision == 0 || State == null || State.Length > 114)
                throw new InvalidDataException("Invalid interaction receipt.");
            var bytes = new byte[14 + State.Length]; bytes[0] = Tag;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), Token); bytes[9] = (byte)Status;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), Revision); State.CopyTo(bytes, 14); return bytes;
        }
        public static ClientInteractionReceipt Decode(byte[] bytes)
        {
            if (!Is(bytes) || bytes.Length > 128) throw new InvalidDataException("Invalid interaction receipt.");
            var receipt = new ClientInteractionReceipt(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1)),
                (ClientInteractionStatus)bytes[9], BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)), bytes.AsSpan(14).ToArray());
            if (receipt.Token == 0 || receipt.Revision == 0 || (byte)receipt.Status > (byte)ClientInteractionStatus.Stale)
                throw new InvalidDataException("Invalid interaction receipt fields.");
            return receipt;
        }
    }
}
