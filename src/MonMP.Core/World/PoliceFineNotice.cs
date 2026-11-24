using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum PoliceFineKind : byte { Speeding = 0, Spitting = 1, Crash = 2, Drug = 3, DrunkDriving = 4 }

    // A receipt for an already stored host ticket. Receiving it never creates a ticket.
    public readonly struct PoliceFineNotice
    {
        public const byte Marker = 0xBE;
        public const int Size = 30;
        public Guid Id { get; }
        public PoliceFineKind Kind { get; }
        public float PlayerSpeed { get; }
        public float SpeedLimit { get; }
        public float Price { get; }

        public PoliceFineNotice(Guid id, PoliceFineKind kind, float playerSpeed, float speedLimit, float price)
        {
            if (id == Guid.Empty || !Enum.IsDefined(typeof(PoliceFineKind), kind) ||
                !Valid(playerSpeed, 10000f) || !Valid(speedLimit, 1000f) || !Valid(price, 100000f))
                throw new InvalidDataException("Invalid police fine notice.");
            Id = id; Kind = kind; PlayerSpeed = playerSpeed; SpeedLimit = speedLimit; Price = price;
        }

        private static bool Valid(float value, float maximum) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= maximum;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Id.ToByteArray()); writer.Write((byte)Kind);
            writer.Write(PlayerSpeed); writer.Write(SpeedLimit); writer.Write(Price);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static PoliceFineNotice Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid police fine notice size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte(); var id = new Guid(reader.ReadBytes(16)); var kind = (PoliceFineKind)reader.ReadByte();
            return new PoliceFineNotice(id, kind, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
