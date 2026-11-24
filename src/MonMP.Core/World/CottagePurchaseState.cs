using System;
using System.IO;

namespace MonMP.Core.World
{
    // Cottage gate and sign presentation follow the host while the one-time cottage purchase is active.
    public readonly struct CottagePurchaseState
    {
        public const ushort Adapter = 32;
        public const int Size = 1 + 28 + 28;
        public static readonly ulong Entity = StaticId.Hash("cottage-purchase-v1");

        public readonly bool Purchased;
        public readonly bool SignVisible;
        public readonly ItemPose Gate;
        public readonly ItemPose Sign;

        public CottagePurchaseState(bool purchased, bool signVisible, ItemPose gate, ItemPose sign)
        {
            Purchased = purchased;
            SignVisible = signVisible;
            Gate = gate;
            Sign = sign;
            Gate.Validate();
            Sign.Validate();
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)((Purchased ? 1 : 0) | (SignVisible ? 2 : 0)));
            Gate.Write(writer);
            Sign.Write(writer);
            return stream.ToArray();
        }

        public static CottagePurchaseState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid cottage purchase state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte flags = reader.ReadByte();
            if ((flags & ~3) != 0) throw new InvalidDataException("Invalid cottage purchase flags.");
            return new CottagePurchaseState((flags & 1) != 0, (flags & 2) != 0, ItemPose.Read(reader), ItemPose.Read(reader));
        }
    }

    // The guest sends the price it saw as cents. The host still checks the native fixed price.
    public readonly struct CottagePurchaseRequest
    {
        public const byte Marker = 0xD8;
        public const int Size = 21;
        public readonly Guid Operation;
        public readonly int PriceCents;

        public CottagePurchaseRequest(Guid operation, int priceCents)
        {
            if (operation == Guid.Empty || priceCents < 0 || priceCents > 1000000000)
                throw new InvalidDataException("Invalid cottage purchase request.");
            Operation = operation;
            PriceCents = priceCents;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(PriceCents);
            return stream.ToArray();
        }

        public static CottagePurchaseRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid cottage purchase request size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            return new CottagePurchaseRequest(operation, reader.ReadInt32());
        }
    }

    public readonly struct CottagePurchaseReply
    {
        public const byte Marker = CottagePurchaseRequest.Marker;
        public const int Size = 18;
        public readonly Guid Operation;
        public readonly PurchaseStatus Status;

        public CottagePurchaseReply(Guid operation, PurchaseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status))
                throw new InvalidDataException("Invalid cottage purchase reply.");
            Operation = operation;
            Status = status;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static CottagePurchaseReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid cottage purchase reply size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            return new CottagePurchaseReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte());
        }
    }
}
