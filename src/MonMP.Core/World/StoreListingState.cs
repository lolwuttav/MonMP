using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public sealed class StoreListingState
    {
        public const ushort Adapter = 9;
        public ushort Prefab { get; }
        public float Price { get; }
        public bool Available { get; }
        public bool Open { get; }
        public bool Multiple { get; }
        public StoreListingState(ushort prefab, float price, bool available, bool open, bool multiple)
        {
            if (prefab == 0 || !ItemPose.Finite(price, 10000000) || price < 0) throw new InvalidDataException("Invalid store listing.");
            Prefab = prefab; Price = price; Available = available; Open = open; Multiple = multiple;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Prefab); writer.Write(Price); writer.Write((byte)((Available ? 1 : 0) | (Open ? 2 : 0) | (Multiple ? 4 : 0))); return stream.ToArray();
        }
        public static StoreListingState Decode(byte[] payload)
        {
            if (payload.Length != 7) throw new InvalidDataException("Invalid store listing size.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            ushort prefab = reader.ReadUInt16(); float price = reader.ReadSingle(); byte flags = reader.ReadByte();
            if (flags > 7) throw new InvalidDataException("Invalid store flags.");
            return new StoreListingState(prefab, price, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
        }
    }
    public sealed class PurchaseRequest
    {
        public Guid Operation { get; }
        public float Price { get; }
        public PurchaseRequest(Guid operation, float price)
        {
            if (operation == Guid.Empty || !ItemPose.Finite(price, 10000000) || price < 0) throw new InvalidDataException("Invalid purchase request.");
            Operation = operation; Price = price;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write(Price); return stream.ToArray();
        }
        public static PurchaseRequest Decode(byte[] payload)
        {
            if (payload.Length != 20) throw new InvalidDataException("Invalid purchase request size.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            return new PurchaseRequest(new Guid(reader.ReadBytes(16)), reader.ReadSingle());
        }
    }
    // Retain claims for the entire host world lifetime. Never evict a claim and permit a delayed retry.
    // Claim before native side effects: even an uncertain exception must not execute twice.
    public sealed class PurchaseClaims
    {
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        public const int Capacity = 16384;
        public bool Claim(Guid profile, Guid operation)
        {
            if (profile == Guid.Empty || operation == Guid.Empty) throw new InvalidDataException("Missing purchase identity.");
            if (claims.Count >= Capacity) return false;
            return claims.Add((profile, operation));
        }
    }
}
