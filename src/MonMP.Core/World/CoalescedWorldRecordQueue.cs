using System;
using System.Collections.Generic;

namespace MonMP.Core.World
{
    // Updating a pending entity retains its place. Once delivered, its next update joins
    // the tail instead of reusing an early Dictionary enumeration slot indefinitely.
    internal sealed class CoalescedWorldRecordQueue
    {
        private const int MaxPooledNodes = 4096;
        private readonly Dictionary<ulong, LinkedListNode<WorldRecord>> pending = new Dictionary<ulong, LinkedListNode<WorldRecord>>();
        private readonly LinkedList<WorldRecord> order = new LinkedList<WorldRecord>();
        private readonly Stack<LinkedListNode<WorldRecord>> available = new Stack<LinkedListNode<WorldRecord>>();
        public int Count => pending.Count;
        public LinkedList<WorldRecord> Values => order;
        public WorldRecord First => order.First?.Value ?? throw new InvalidOperationException("No pending world records.");
        public bool ContainsKey(ulong id) => pending.ContainsKey(id);
        public WorldRecord this[ulong id]
        {
            set
            {
                if (value.Id != id) throw new ArgumentException("Mismatched pending world identity.", nameof(value));
                if (pending.TryGetValue(id, out var node)) { node.Value = value; return; }
                node = available.Count == 0 ? new LinkedListNode<WorldRecord>(value) : available.Pop();
                node.Value = value; order.AddLast(node); pending.Add(id, node);
            }
        }
        public bool Remove(ulong id)
        {
            if (!pending.TryGetValue(id, out var node)) return false;
            pending.Remove(id); order.Remove(node);
            // Keep a bounded reusable node pool without retaining old payloads.
            node.Value = null!;
            if (available.Count < MaxPooledNodes) available.Push(node);
            return true;
        }
        public void Clear()
        {
            while (order.First != null) Remove(order.First.Value.Id);
        }
    }
}
