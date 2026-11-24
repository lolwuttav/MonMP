using System;
using System.Globalization;
using System.IO;

namespace MonMP.Core.World
{
    public enum PaintOwnerKind : byte { Item = 1, PartSocket = 2 }

    // Item and socket identities already belong to their respective replication adapters.
    // This separate dynamic record never changes the save-independent static paint catalog.
    public sealed class DynamicPaintPanelState
    {
        public const ushort Adapter = 51;
        private const byte Marker = 0xDA;
        private const byte Version = 1;
        public PaintOwnerKind OwnerKind { get; }
        public ulong OwnerId { get; }
        public ushort Prefab { get; }
        public PaintPanelState? Paint { get; }

        public DynamicPaintPanelState(PaintOwnerKind ownerKind, ulong ownerId, ushort prefab, PaintPanelState? paint)
        {
            if (!Enum.IsDefined(typeof(PaintOwnerKind), ownerKind) || ownerId == 0 ||
                (paint == null) != (prefab == 0))
                throw new InvalidDataException("Invalid dynamic paint owner or presence.");
            OwnerKind = ownerKind; OwnerId = ownerId; Prefab = prefab; Paint = paint;
        }

        public static ulong RecordId(PaintOwnerKind kind, ulong ownerId)
        {
            if (!Enum.IsDefined(typeof(PaintOwnerKind), kind) || ownerId == 0)
                throw new InvalidDataException("Invalid dynamic paint owner.");
            ulong id = StaticId.Hash("dynamic-paint-panel-v1/" + ((byte)kind).ToString(CultureInfo.InvariantCulture) +
                "/" + ownerId.ToString("X16", CultureInfo.InvariantCulture)) |
                WorldRecord.DynamicIdStart;
            return id == WorldRecord.DynamicIdStart ? id + 1 : id;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write((byte)OwnerKind);
            writer.Write(OwnerId); writer.Write(Prefab);
            if (Paint != null) writer.Write(Paint.Encode());
            return stream.ToArray();
        }

        public bool SameState(DynamicPaintPanelState? other) => other != null &&
            OwnerKind == other.OwnerKind && OwnerId == other.OwnerId && Prefab == other.Prefab &&
            (Paint == null ? other.Paint == null : Paint.SameState(other.Paint));

        public static DynamicPaintPanelState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 13 || payload.Length > 13 + PaintPanelState.MaxPayloadSize)
                throw new InvalidDataException("Invalid dynamic paint record size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker || reader.ReadByte() != Version)
                throw new InvalidDataException("Unsupported dynamic paint record.");
            var kind = (PaintOwnerKind)reader.ReadByte();
            ulong owner = reader.ReadUInt64();
            ushort prefab = reader.ReadUInt16();
            var remaining = reader.ReadBytes((int)(stream.Length - stream.Position));
            return new DynamicPaintPanelState(kind, owner, prefab,
                remaining.Length == 0 ? null : PaintPanelState.Decode(remaining));
        }
    }
}
