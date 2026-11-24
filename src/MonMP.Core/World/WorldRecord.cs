using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public sealed class WorldRecord
    {
        public const int MaxPayloadBytes = 512;
        public const ulong DynamicIdStart = 0x8000000000000000;
        public ulong Id { get; }
        public uint Revision { get; }
        public ushort Adapter { get; }
        private readonly byte[] payload;
        public byte[] Payload => (byte[])payload.Clone();
        // Borrow immutable bytes for comparisons; callers that need an owned,
        // writable array keep using Payload's detached copy.
        public ReadOnlySpan<byte> PayloadSpan => payload;
        public WorldRecord(ulong id, uint revision, ushort adapter, byte[] payload) : this(id, revision, adapter, payload, false) { }
        private WorldRecord(ulong id, uint revision, ushort adapter, byte[] payload, bool ownsPayload)
        {
            if (id == 0 || revision == 0 || adapter == 0 || payload.Length > MaxPayloadBytes) throw new InvalidDataException("Invalid entity record.");
            Id = id; Revision = revision; Adapter = adapter; this.payload = ownsPayload ? payload : (byte[])payload.Clone();
        }
        public WorldRecord Next(byte[] value) => new WorldRecord(Id, Revision == uint.MaxValue ? 1 : Revision + 1, Adapter, value);
        public bool SameValue(byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return payload.AsSpan().SequenceEqual(value);
        }
        public void Write(BinaryWriter w) { w.Write(Id); w.Write(Revision); w.Write(Adapter); w.Write((ushort)payload.Length); w.Write(payload); }
        public static WorldRecord Read(BinaryReader r)
        {
            ulong id = r.ReadUInt64(); uint revision = r.ReadUInt32(); ushort adapter = r.ReadUInt16(); int length = r.ReadUInt16();
            if (length > MaxPayloadBytes || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid entity payload length.");
            // The ordinary reader allocates a detached array. A custom reader can
            // override ReadBytes, so retain public ownership isolation for it.
            return new WorldRecord(id, revision, adapter, r.ReadBytes(length), r.GetType() == typeof(BinaryReader));
        }
        public static string Digest(IEnumerable<WorldRecord> records, bool catalogOnly = false) => DigestOrdered(records.OrderBy(r => r.Id), catalogOnly);
        // Channel baselines already have ascending IDs. Keep that order through
        // peer-specific substitutions instead of sorting the same baseline again.
        internal static string DigestOrdered(IEnumerable<WorldRecord> records, bool catalogOnly = false)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[4096];
            int buffered = 0;
            foreach (var record in records)
            {
                int headerBytes = catalogOnly ? 10 : 16;
                int payloadBytes = catalogOnly ? 0 : record.payload.Length;
                int recordBytes = headerBytes + payloadBytes;
                if (buffer.Length - buffered < recordBytes)
                {
                    hash.AppendData(buffer, 0, buffered);
                    buffered = 0;
                }

                int headerOffset = buffered;
                Span<byte> header = buffer.AsSpan(headerOffset, headerBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0, 8), record.Id);
                if (catalogOnly) BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(8, 2), record.Adapter);
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), record.Revision);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12, 2), record.Adapter);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14, 2), (ushort)record.payload.Length);
                    if (payloadBytes > 0) Buffer.BlockCopy(record.payload, 0, buffer, headerOffset + headerBytes, payloadBytes);
                }
                buffered += recordBytes;
            }
            if (buffered > 0) hash.AppendData(buffer, 0, buffered);
            return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "");
        }
    }

    public sealed class SnapshotReceiver
    {
        public const int MaxEntities = 100000;
        private readonly Dictionary<ulong, ushort> catalog;
        private readonly HashSet<ushort> dynamicAdapters;
        private int expectedCount;
        private Dictionary<ulong, WorldRecord>? staging;
        private string expectedDigest = "";
        private SnapshotScope scope;
        private Dictionary<ulong, WorldRecord> committed = new Dictionary<ulong, WorldRecord>();
        public IReadOnlyDictionary<ulong, WorldRecord> Records { get; private set; }
        public bool Ready { get; private set; }
        public bool Receiving => staging != null;
        public int ReceivedCount => staging?.Count ?? 0;
        public int ExpectedCount => staging == null ? 0 : expectedCount;
        // The catalog is immutable after construction. Return a detached descriptor
        // copy for optional manifest export without retaining initial record payloads.
        internal IReadOnlyList<KeyValuePair<ulong, ushort>> CatalogDescriptors => catalog
            .OrderBy(pair => pair.Key).ToArray();
        public SnapshotReceiver(IEnumerable<WorldRecord> catalog, IEnumerable<ushort>? dynamicAdapters = null)
        {
            this.dynamicAdapters = new HashSet<ushort>(dynamicAdapters ?? Array.Empty<ushort>());
            this.catalog = catalog.ToDictionary(r => r.Id, r => r.Adapter);
            Records = new ReadOnlyDictionary<ulong, WorldRecord>(committed);
            if (this.catalog.Count > MaxEntities) throw new InvalidDataException("Entity catalog too large.");
        }
        public void Begin(int count, string digest) => Begin(SnapshotScope.World, count, digest);
        public void Begin(SnapshotScope scope, int count, string digest)
        {
            ValidateScope(scope);
            int required = catalog.Count(pair => Matches(scope, pair.Key, pair.Value));
            if (staging != null || (!Ready && scope.Kind != SnapshotScopeKind.World) || count < required || count > MaxEntities ||
                (scope.Kind == SnapshotScopeKind.Entity && count != 1) ||
                (scope.Kind != SnapshotScopeKind.Entity && !HasDynamic(scope) && count != required) ||
                digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid snapshot begin.");
            this.scope = scope;
            expectedCount = count;
            staging = new Dictionary<ulong, WorldRecord>(); expectedDigest = digest;
        }
        public void Add(WorldRecord record)
        {
            Validate(record);
            if (staging == null || !scope.Contains(record) || staging.Count >= expectedCount || staging.ContainsKey(record.Id)) throw new InvalidDataException("Unexpected or duplicate snapshot record.");
            staging.Add(record.Id, record);
        }
        public IReadOnlyDictionary<ulong, WorldRecord> Commit()
        {
            if (staging == null || staging.Count != expectedCount || catalog.Any(pair => Matches(scope, pair.Key, pair.Value) && !staging.ContainsKey(pair.Key)) || WorldRecord.Digest(staging.Values) != expectedDigest)
                throw new InvalidDataException("Incomplete or corrupt snapshot.");
            var next = scope.Kind == SnapshotScopeKind.World ? new Dictionary<ulong, WorldRecord>() :
                committed.Values.Where(r => !scope.Contains(r)).ToDictionary(r => r.Id);
            if (next.Count + staging.Count > MaxEntities) throw new InvalidDataException("Entity catalog too large.");
            foreach (var record in staging.Values) next.Add(record.Id, record);
            var applied = new ReadOnlyDictionary<ulong, WorldRecord>(staging);
            committed = next; Records = new ReadOnlyDictionary<ulong, WorldRecord>(committed);
            staging = null; Ready = true;
            return applied;
        }
        public bool Apply(WorldRecord record)
        {
            Validate(record);
            if (!Ready || Receiving) throw new InvalidDataException("Delta before snapshot completion.");
            if (Records.TryGetValue(record.Id, out var previous))
            {
                if (previous.Adapter != record.Adapter) throw new InvalidDataException("Entity adapter changed.");
                if (!Wire.IsNewer(record.Revision, previous.Revision)) return false;
            }
            else if (Records.Count >= MaxEntities) throw new InvalidDataException("Entity catalog too large.");
            committed[record.Id] = record; return true;
        }
        private static bool Matches(SnapshotScope scope, ulong id, ushort adapter) => scope.Kind == SnapshotScopeKind.World ||
            (adapter == scope.Adapter && (scope.Kind == SnapshotScopeKind.Section || id == scope.Entity));
        private bool HasDynamic(SnapshotScope scope) => scope.Kind == SnapshotScopeKind.World ? dynamicAdapters.Count > 0 : dynamicAdapters.Contains(scope.Adapter);
        public void ValidateScope(SnapshotScope scope)
        {
            if (scope.Kind == SnapshotScopeKind.World) return;
            if (!catalog.Values.Contains(scope.Adapter) && !dynamicAdapters.Contains(scope.Adapter)) throw new InvalidDataException("Unknown snapshot section.");
            if (scope.Kind == SnapshotScopeKind.Entity)
            {
                if (catalog.TryGetValue(scope.Entity, out ushort adapter))
                { if (adapter != scope.Adapter) throw new InvalidDataException("Wrong snapshot entity adapter."); }
                else if (scope.Entity <= WorldRecord.DynamicIdStart || !dynamicAdapters.Contains(scope.Adapter)) throw new InvalidDataException("Unknown snapshot entity.");
            }
        }
        private void Validate(WorldRecord record)
        {
            if (catalog.TryGetValue(record.Id, out ushort adapter))
            { if (record.Adapter != adapter) throw new InvalidDataException("Wrong static adapter."); }
            else if (record.Id <= WorldRecord.DynamicIdStart || !dynamicAdapters.Contains(record.Adapter)) throw new InvalidDataException("Unknown entity or adapter.");
        }
    }
}
