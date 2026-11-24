using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum SnapshotScopeKind : byte { World, Section, Entity }

    // A section is the state owned by one registered adapter. Static IDs may have
    // their high bit set, so an entity scope always carries its adapter explicitly.
    public readonly struct SnapshotScope
    {
        public SnapshotScopeKind Kind { get; }
        public ushort Adapter { get; }
        public ulong Entity { get; }
        private SnapshotScope(SnapshotScopeKind kind, ushort adapter, ulong entity)
        {
            if (!Enum.IsDefined(typeof(SnapshotScopeKind), kind) ||
                (kind == SnapshotScopeKind.World && (adapter != 0 || entity != 0)) ||
                (kind != SnapshotScopeKind.World && adapter == 0) ||
                (kind == SnapshotScopeKind.Section && entity != 0) ||
                (kind == SnapshotScopeKind.Entity && entity == 0))
                throw new InvalidDataException("Invalid snapshot scope.");
            Kind = kind; Adapter = adapter; Entity = entity;
        }
        public static SnapshotScope World => default;
        public static SnapshotScope Section(ushort adapter) => new SnapshotScope(SnapshotScopeKind.Section, adapter, 0);
        public static SnapshotScope ForEntity(ushort adapter, ulong entity) => new SnapshotScope(SnapshotScopeKind.Entity, adapter, entity);
        public bool Contains(WorldRecord record) => Kind == SnapshotScopeKind.World ||
            (record.Adapter == Adapter && (Kind == SnapshotScopeKind.Section || record.Id == Entity));
        public void Write(BinaryWriter writer) { writer.Write((byte)Kind); writer.Write(Adapter); writer.Write(Entity); }
        public static SnapshotScope Read(BinaryReader reader) => new SnapshotScope((SnapshotScopeKind)reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt64());
        public override string ToString() => Kind == SnapshotScopeKind.World ? "world" :
            Kind == SnapshotScopeKind.Section ? "section " + Adapter : "entity " + Entity.ToString("X16");
    }
}
