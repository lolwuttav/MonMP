using System;
using System.Collections.Generic;
using System.Linq;

namespace MonMP.Core.World
{
    public readonly struct ItemLease
    {
        public const uint Unowned = uint.MaxValue;
        public readonly uint Owner, Generation;
        public ItemLease(uint owner, uint generation) { Owner = owner; Generation = generation; }
    }

    // Host-only ownership. Movement revisions and ownership generations are independent.
    public sealed class ItemLeaseTable
    {
        private readonly Dictionary<ulong, ItemLease> leases = new Dictionary<ulong, ItemLease>();
        private readonly Dictionary<uint, HashSet<ulong>> held = new Dictionary<uint, HashSet<ulong>>();
        public ItemLease this[ulong id] => leases[id];
        public void Add(ulong id) => leases.Add(id, new ItemLease(ItemLease.Unowned, 1));
        public void Remove(ulong id) { var lease = leases[id]; if (lease.Owner != ItemLease.Unowned) Release(id, lease.Owner, lease.Generation); }
        // A hand holds one item; an equipped tool may additionally hold one item of its own (a wrench's bolt).
        public bool TryAcquire(ulong id, uint peer, uint expectedGeneration, int maxHeld = 1)
        {
            if (peer == ItemLease.Unowned || !leases.TryGetValue(id, out var lease) || lease.Owner != ItemLease.Unowned ||
                lease.Generation != expectedGeneration || HeldBy(peer) >= maxHeld) return false;
            leases[id] = new ItemLease(peer, Next(lease.Generation));
            if (!held.TryGetValue(peer, out var set)) held.Add(peer, set = new HashSet<ulong>());
            set.Add(id); return true;
        }
        public int HeldBy(uint peer) => held.TryGetValue(peer, out var set) ? set.Count : 0;
        public bool Owns(ulong id, uint peer, uint generation) => peer != ItemLease.Unowned && leases.TryGetValue(id, out var lease) && lease.Owner == peer && lease.Generation == generation;
        // Item mutations that arrive after a local native action (such as unpacking a pack)
        // require the actor to still own the exact generation they acted on.
        public bool CanAct(ulong id, uint peer, uint generation) => Owns(id, peer, generation);
        public bool Release(ulong id, uint peer, uint generation)
        {
            if (!Owns(id, peer, generation)) return false;
            leases[id] = new ItemLease(ItemLease.Unowned, Next(generation));
            if (held.TryGetValue(peer, out var set) && set.Remove(id) && set.Count == 0) held.Remove(peer);
            return true;
        }
        public ulong[] ReleasePeer(uint peer)
        {
            var ids = leases.Where(p => p.Value.Owner == peer && peer != ItemLease.Unowned).Select(p => p.Key).ToArray();
            foreach (var id in ids) Release(id, peer, leases[id].Generation);
            return ids;
        }
        private static uint Next(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;
    }
}
