using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public sealed class ConsumptionReceipt
    {
        public Guid Consumer { get; }
        public uint Generation { get; }
        public bool Acknowledged { get; }
        public ConsumptionReceipt(Guid consumer, uint generation, bool acknowledged = false)
        {
            if (consumer == Guid.Empty || generation == 0) throw new InvalidDataException("Invalid consumption receipt.");
            Consumer = consumer; Generation = generation; Acknowledged = acknowledged;
        }
        public void Write(BinaryWriter w) { w.Write(Consumer.ToByteArray()); w.Write(Generation); w.Write(Acknowledged); }
        public static ConsumptionReceipt Read(BinaryReader r)
        {
            var consumer = new Guid(r.ReadBytes(16)); uint generation = r.ReadUInt32(); byte acknowledged = r.ReadByte();
            if (acknowledged > 1) throw new InvalidDataException("Invalid receipt acknowledgment.");
            return new ConsumptionReceipt(consumer, generation, acknowledged == 1);
        }
    }

    public sealed class ConsumptionCommit
    {
        public ConsumptionReceipt Receipt { get; }
        public bool? Applied { get; private set; }
        public ConsumptionCommit(ConsumptionReceipt receipt) => Receipt = receipt;
        public bool Complete(Guid consumer, uint generation, bool applied)
        {
            if (Applied.HasValue || Receipt.Consumer != consumer || Receipt.Generation != generation) return false;
            Applied = applied; return true;
        }
    }

    // Retained by the plugin across disconnect/reconnect, scoped by the host epoch.
    // This prevents replay within a running game; it is not crash-durable player persistence.
    public sealed class ConsumptionInbox
    {
        private sealed class Entry { public ushort Prefab; public bool? Applied; }
        private readonly Dictionary<(Guid Epoch, ulong Entity, uint Generation), Entry> entries = new Dictionary<(Guid, ulong, uint), Entry>();
        public void Clear() => entries.Clear();
        public void Track(Guid epoch, ulong entity, uint generation, ushort prefab)
        {
            if (epoch == Guid.Empty || entity <= WorldRecord.DynamicIdStart || generation == 0 || prefab == 0)
                throw new InvalidDataException("Invalid consumption request.");
            var key = (epoch, entity, generation);
            if (entries.TryGetValue(key, out var entry))
            { if (entry.Prefab != prefab) throw new InvalidDataException("Consumption prefab changed."); return; }
            if (entries.Count >= SnapshotReceiver.MaxEntities) throw new InvalidOperationException("Consumption history is full; restart the game before consuming more items.");
            entries.Add(key, new Entry { Prefab = prefab });
        }
        public bool Deliver(Guid epoch, ulong entity, uint generation, ushort prefab, Func<bool> apply, out bool applied)
        {
            applied = false;
            if (!entries.TryGetValue((epoch, entity, generation), out var entry)) return false;
            if (entry.Prefab != prefab) throw new InvalidDataException("Consumption receipt prefab mismatch.");
            if (!entry.Applied.HasValue)
            {
                // Claim before invoking native effects. An exception may have partially applied
                // them, so an uncertain attempt must never be retried as a fresh effect.
                entry.Applied = true;
                entry.Applied = apply();
            }
            applied = entry.Applied.Value; return true;
        }
    }
}
