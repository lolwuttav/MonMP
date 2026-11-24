using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct BunkerCompostState
    {
        public const ushort Adapter = 157;
        public const int Size = 8;
        public static readonly ulong Entity = StaticId.Hash("bunker-compost-contents-v1");
        public readonly float Compost, Fertilizer;
        public BunkerCompostState(float compost, float fertilizer)
        {
            if (!float.IsFinite(compost) || compost < 0 || compost > 1000 ||
                !float.IsFinite(fertilizer) || fertilizer < 0 || fertilizer > 1000)
                throw new InvalidDataException("Invalid bunker compost contents.");
            Compost = compost; Fertilizer = fertilizer;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Compost); writer.Write(Fertilizer);
            return stream.ToArray();
        }
        public static BunkerCompostState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != Size) throw new InvalidDataException("Invalid bunker compost state size.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            return new BunkerCompostState(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
