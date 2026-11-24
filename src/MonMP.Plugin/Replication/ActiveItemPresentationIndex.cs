using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Protocol bindings retain tombstones for the life of a session. Presentation only
    // needs held objects; keep its cost proportional to those objects, not save size.
    internal sealed class ActiveItemPresentationIndex<T> where T : class
    {
        private sealed class Entry
        {
            internal readonly ulong Id;
            internal readonly long Order;
            internal readonly T Value;
            internal uint Owner;
            internal bool Tool, Remote;
            internal Entry(ulong id, long order, T value) { Id = id; Order = order; Value = value; Owner = uint.MaxValue; }
        }

        private readonly Dictionary<ulong, Entry> active = new Dictionary<ulong, Entry>();
        private readonly Dictionary<uint, List<Entry>> held = new Dictionary<uint, List<Entry>>();
        private readonly List<Entry> remote = new List<Entry>();

        internal int Count => active.Count;
        internal int RemoteCount => remote.Count;

        internal void Update(ulong id, long order, T value, uint owner, bool tool, bool remoteOwned)
        {
            if (owner == uint.MaxValue && !remoteOwned) { Remove(id); return; }
            if (!active.TryGetValue(id, out var entry))
                active.Add(id, entry = new Entry(id, order, value));
            if (entry.Owner != owner)
            {
                RemoveHeld(entry);
                entry.Owner = owner;
                if (owner != uint.MaxValue)
                {
                    if (!held.TryGetValue(owner, out var owned)) held.Add(owner, owned = new List<Entry>(2));
                    InsertInOrder(owned, entry);
                }
            }
            entry.Tool = tool;
            if (entry.Remote != remoteOwned)
            {
                if (remoteOwned) InsertInOrder(remote, entry);
                else remote.Remove(entry);
                entry.Remote = remoteOwned;
            }
        }

        internal bool TrySelect(uint owner, out T? value, out bool tool)
        {
            value = null; tool = false;
            if (!held.TryGetValue(owner, out var owned)) return false;
            foreach (var entry in owned)
                if (value == null || entry.Tool && !tool) { value = entry.Value; tool = entry.Tool; }
            return value != null;
        }

        // A fishing rod and its attached result can hold the same owner's two leases.
        // Never guess between additional objects during a repaired/transitioning snapshot.
        internal bool TrySelectOther(uint owner, T excluded, out T? value)
        {
            value = null;
            if (!held.TryGetValue(owner, out var owned)) return false;
            foreach (var entry in owned)
            {
                if (ReferenceEquals(entry.Value, excluded)) continue;
                if (value != null) { value = null; return false; }
                value = entry.Value;
            }
            return value != null;
        }

        // Native apply/lease callbacks may change the index during presentation. A
        // reusable detached snapshot retains the original pass's order and members.
        internal void SnapshotRemote(List<T> destination)
        {
            destination.Clear();
            foreach (var entry in remote) destination.Add(entry.Value);
        }

        internal void Remove(ulong id)
        {
            if (!active.TryGetValue(id, out var entry)) return;
            RemoveHeld(entry);
            if (entry.Remote) remote.Remove(entry);
            active.Remove(id);
        }

        internal void Clear() { active.Clear(); held.Clear(); remote.Clear(); }

        private void RemoveHeld(Entry entry)
        {
            if (entry.Owner == uint.MaxValue || !held.TryGetValue(entry.Owner, out var owned)) return;
            owned.Remove(entry);
            if (owned.Count == 0) held.Remove(entry.Owner);
        }

        private static void InsertInOrder(List<Entry> entries, Entry entry)
        {
            int at = entries.Count;
            while (at > 0 && entries[at - 1].Order > entry.Order) at--;
            entries.Insert(at, entry);
        }
    }
}
