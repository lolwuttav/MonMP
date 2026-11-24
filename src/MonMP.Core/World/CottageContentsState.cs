using System;
using System.IO;

namespace MonMP.Core.World
{
    // Host snapshot of the contents that Cottage.LoadFromSave normally initializes for an owned cottage.
    public readonly struct CottageContentsState
    {
        public const ushort Adapter = 35;
        public const int Size = 1 + 4 + 4 + 28 + 4;
        public static readonly ulong Entity = StaticId.Hash("cottage-contents-v1");

        public readonly bool Purchased;
        public readonly float Compost;
        public readonly float Fertilizer;
        public readonly ItemPose Wheelbarrow;
        public readonly bool CoffeeSeedsActive;
        public readonly int PercolatorCoffeeAmount;

        public CottageContentsState(bool purchased, float compost, float fertilizer, ItemPose wheelbarrow,
            bool coffeeSeedsActive, int percolatorCoffeeAmount)
        {
            if (!float.IsFinite(compost) || compost < 0f || compost > 1000f ||
                !float.IsFinite(fertilizer) || fertilizer < 0f || fertilizer > 1000f)
                throw new InvalidDataException("Invalid cottage compost or fertilizer amount.");
            if (percolatorCoffeeAmount < 0 || percolatorCoffeeAmount > 1000)
                throw new InvalidDataException("Invalid cottage percolator amount.");
            wheelbarrow.Validate();
            Purchased = purchased;
            Compost = compost;
            Fertilizer = fertilizer;
            Wheelbarrow = wheelbarrow;
            CoffeeSeedsActive = coffeeSeedsActive;
            PercolatorCoffeeAmount = percolatorCoffeeAmount;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)((Purchased ? 1 : 0) | (CoffeeSeedsActive ? 2 : 0)));
            writer.Write(Compost);
            writer.Write(Fertilizer);
            Wheelbarrow.Write(writer);
            writer.Write(PercolatorCoffeeAmount);
            return stream.ToArray();
        }

        public static CottageContentsState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid cottage contents state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte flags = reader.ReadByte();
            if ((flags & ~3) != 0) throw new InvalidDataException("Invalid cottage contents flags.");
            return new CottageContentsState((flags & 1) != 0, reader.ReadSingle(), reader.ReadSingle(),
                ItemPose.Read(reader), (flags & 2) != 0, reader.ReadInt32());
        }

        public bool SameAs(CottageContentsState other) => Purchased == other.Purchased &&
            Compost == other.Compost && Fertilizer == other.Fertilizer && Wheelbarrow.Equals(other.Wheelbarrow) &&
            CoffeeSeedsActive == other.CoffeeSeedsActive && PercolatorCoffeeAmount == other.PercolatorCoffeeAmount;
    }
}
