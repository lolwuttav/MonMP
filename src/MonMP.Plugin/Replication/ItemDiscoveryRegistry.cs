using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Stable nodes keep a callback's Add/Remove/Clear from invalidating a pass. No
    // whole-registry copy or per-frame allocation; removed queued nodes lose their item.
    internal sealed class ItemDiscoveryRegistry<T> where T : class
    {
        private sealed class Entry
        {
            internal T? Item;
            internal bool Dirty;
            internal long SweepPass;
            internal LinkedListNode<Entry>? Node;
        }
        private readonly Dictionary<T, Entry> entries = new Dictionary<T, Entry>();
        private readonly LinkedList<Entry> order = new LinkedList<Entry>();
        private readonly Queue<Entry> dirty = new Queue<Entry>();
        private LinkedListNode<Entry>? cursor;
        private long sweepPass;
        private readonly Func<T, bool> alive;
        internal int Count => entries.Count;
        internal bool Contains(T item) => entries.ContainsKey(item);
        internal ItemDiscoveryRegistry(Func<T, bool> alive) => this.alive = alive;
        internal bool Add(T item, bool changed = true)
        {
            if (!alive(item)) return false;
            bool added = !entries.TryGetValue(item, out var entry);
            if (added)
            {
                entry = new Entry { Item = item };
                entry.Node = order.AddLast(entry);
                entries.Add(item, entry);
            }
            if (changed && !entry!.Dirty) { entry.Dirty = true; dirty.Enqueue(entry); }
            return added;
        }
        internal void Remove(T item)
        {
            if (!entries.TryGetValue(item, out var entry)) return;
            entries.Remove(item);
            if (cursor == entry.Node) cursor = entry.Node!.Next ?? order.First;
            order.Remove(entry.Node!);
            if (order.Count == 0) cursor = null;
            entry.Item = null; entry.Node = null;
        }
        internal void Clear()
        {
            foreach (var entry in order) { entry.Item = null; entry.Node = null; }
            entries.Clear(); order.Clear(); dirty.Clear(); cursor = null;
        }
        internal int DrainDirty(Action<T> visit, int limit = 256)
        {
            // A callback can dirty itself again; leave that event for the next frame.
            int count = Math.Min(limit, dirty.Count), visited = 0;
            while (count-- > 0 && dirty.Count != 0)
            {
                var entry = dirty.Dequeue(); entry.Dirty = false;
                var item = entry.Item;
                if (item == null) continue;
                if (!alive(item)) { Remove(item); continue; }
                visit(item); visited++;
            }
            return visited;
        }
        internal int Sweep(int count, Action<T> visit)
        {
            count = Math.Min(count, order.Count);
            long pass = ++sweepPass;
            int visited = 0;
            while (count-- > 0 && order.Count != 0)
            {
                var node = cursor ?? order.First!;
                cursor = node.Next ?? order.First;
                if (node.Value.SweepPass == pass) break;
                node.Value.SweepPass = pass;
                var item = node.Value.Item;
                if (item == null) continue;
                if (!alive(item)) { Remove(item); continue; }
                visit(item); visited++;
            }
            return visited;
        }
    }
}
