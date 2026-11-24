using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum HydroMailKind : byte { Bill = 1, StartConstruction = 2, PoliceTicket = 4 }

    // The request identifies a leased, signed envelope. Price and account state are
    // deliberately absent: the host derives them from its own native envelope and ledger.
    public readonly struct HydroMailRequest
    {
        public const byte Marker = 0xD9;
        public const int Size = 30;
        public readonly Guid Operation;
        public readonly ulong Envelope;
        public readonly uint Generation;
        public readonly HydroMailKind Kind;

        public HydroMailRequest(Guid operation, ulong envelope, uint generation, HydroMailKind kind)
        {
            if (operation == Guid.Empty || envelope <= WorldRecord.DynamicIdStart || generation == 0 ||
                !Enum.IsDefined(typeof(HydroMailKind), kind))
                throw new InvalidDataException("Invalid hydro mail request.");
            Operation = operation; Envelope = envelope; Generation = generation; Kind = kind;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Envelope); writer.Write(Generation); writer.Write((byte)Kind);
            return stream.ToArray();
        }

        public static HydroMailRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid hydro mail request size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            return new HydroMailRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(), (HydroMailKind)reader.ReadByte());
        }
    }

    // A guest signed the leased letter it holds; the host signs its own copy so the signature survives.
    public readonly struct HydroMailSign
    {
        public const byte Marker = 0xDA;
        public const int Size = 13;
        public readonly ulong Envelope;
        public readonly uint Generation;

        public HydroMailSign(ulong envelope, uint generation)
        {
            if (envelope <= WorldRecord.DynamicIdStart || generation == 0) throw new InvalidDataException("Invalid hydro mail signature.");
            Envelope = envelope; Generation = generation;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            var payload = new byte[Size];
            payload[0] = Marker;
            BitConverter.GetBytes(Envelope).CopyTo(payload, 1);
            BitConverter.GetBytes(Generation).CopyTo(payload, 9);
            return payload;
        }

        public static HydroMailSign Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid hydro mail signature size.");
            return new HydroMailSign(BitConverter.ToUInt64(payload, 1), BitConverter.ToUInt32(payload, 9));
        }
    }

    public enum HydroMailStatus : byte
    {
        Accepted = 1, Unsigned, TooFar, NoFunds, Busy, Stale, Unsupported, SaveBlocked, Capacity, NativeRejected, RetryRejected, Uncertain
    }

    public enum HydroMailClaimResult : byte { Claimed, Duplicate, Capacity }

    // Host session claims are retained until that world closes so a delayed envelope
    // request cannot debit the shared cash balance or apply its native hydro effect twice.
    public sealed class HydroMailClaims
    {
        public const int Capacity = 16384;
        private readonly System.Collections.Generic.HashSet<(Guid Profile, Guid Operation)> claims =
            new System.Collections.Generic.HashSet<(Guid, Guid)>();

        public HydroMailClaimResult Claim(Guid profile, Guid operation)
        {
            if (profile == Guid.Empty || operation == Guid.Empty)
                throw new InvalidDataException("Invalid hydro mail operation identity.");
            var key = (profile, operation);
            if (claims.Contains(key)) return HydroMailClaimResult.Duplicate;
            if (claims.Count >= Capacity) return HydroMailClaimResult.Capacity;
            claims.Add(key);
            return HydroMailClaimResult.Claimed;
        }
    }

    public readonly struct HydroMailReply
    {
        public const int Size = 26;
        public readonly Guid Operation;
        public readonly ulong Envelope;
        public readonly HydroMailStatus Status;

        public HydroMailReply(Guid operation, ulong envelope, HydroMailStatus status)
        {
            if (operation == Guid.Empty || envelope <= WorldRecord.DynamicIdStart ||
                !Enum.IsDefined(typeof(HydroMailStatus), status))
                throw new InvalidDataException("Invalid hydro mail reply.");
            Operation = operation; Envelope = envelope; Status = status;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == HydroMailRequest.Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(HydroMailRequest.Marker); writer.Write(Operation.ToByteArray()); writer.Write(Envelope); writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static HydroMailReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid hydro mail reply size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            return new HydroMailReply(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), (HydroMailStatus)reader.ReadByte());
        }
    }
}
