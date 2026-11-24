using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Protocol identities/receipts are permanent; native update work lasts only as long as
    // an object (or the host's final removal capture). Snapshots permit synchronous callbacks.
    internal sealed class ActiveItemWorkIndex<T> where T : class
    {
        private readonly Dictionary<ulong, T> entries = new Dictionary<ulong, T>();
        internal sealed class Buffer
        {
            internal T[] Items = Array.Empty<T>();
            internal int Count, Readers;
            internal View? Lease;
        }
        // A view must be disposed before it leaves the synchronous pass. Nested callbacks
        // may borrow the same membership or a newer one without rewriting an active view.
        internal sealed class View : IDisposable
        {
            internal static readonly View Empty = new View();
            private readonly ActiveItemWorkIndex<T>? owner;
            private readonly Buffer? buffer;
            private View() { }
            internal View(ActiveItemWorkIndex<T> owner, Buffer buffer) { this.owner = owner; this.buffer = buffer; }
            internal int Length => buffer?.Count ?? 0;
            internal ReadOnlyMemory<T> Memory => buffer == null ? default : new ReadOnlyMemory<T>(buffer.Items, 0, buffer.Count);
            internal ReadOnlySpan<T> Span => Memory.Span;
            public void Dispose() { if (buffer != null) owner!.Release(buffer); }
        }
        private readonly List<Buffer> buffers = new List<Buffer>();
        private Buffer? snapshot;
        private bool snapshotDirty;
        internal int Count => entries.Count;
        internal void Set(ulong id, T value, bool active)
        {
            if (active)
            {
                if (entries.TryGetValue(id, out var current) && ReferenceEquals(current, value)) return;
                entries[id] = value; snapshotDirty = true;
            }
            else if (entries.Remove(id)) snapshotDirty = true;
        }
        // The binding objects themselves remain live, as with the detached List snapshot.
        // Refill an idle buffer on membership changes; allocate only for growth/nesting.
        internal View ReadSnapshot()
        {
            if (entries.Count == 0)
            {
                if (snapshot != null || snapshotDirty)
                {
                    snapshot = null; snapshotDirty = false;
                    foreach (var idle in buffers) if (idle.Readers == 0) ClearBuffer(idle);
                }
                return View.Empty;
            }
            if (snapshotDirty)
            {
                Buffer? next = snapshot != null && snapshot.Readers == 0 ? snapshot : null;
                if (next == null)
                    foreach (var available in buffers)
                        if (available.Readers == 0) { next = available; break; }
                if (next == null) { next = new Buffer(); buffers.Add(next); }
                if (next.Items.Length < entries.Count)
                    next.Items = new T[Math.Max(entries.Count, Math.Max(16, next.Items.Length * 2))];
                else if (next.Count > entries.Count) Array.Clear(next.Items, entries.Count, next.Count - entries.Count);
                entries.Values.CopyTo(next.Items, 0);
                next.Count = entries.Count;
                snapshot = next;
                snapshotDirty = false;
            }
            snapshot!.Readers++;
            return snapshot.Lease ??= new View(this, snapshot);
        }
        private void Release(Buffer buffer)
        {
            if (buffer.Readers <= 0) throw new InvalidOperationException("Item work view was already released.");
            if (--buffer.Readers == 0 && buffer != snapshot) ClearBuffer(buffer);
        }
        private static void ClearBuffer(Buffer buffer)
        { Array.Clear(buffer.Items, 0, buffer.Count); buffer.Count = 0; }
        internal void Snapshot(List<T> destination)
        { destination.Clear(); destination.AddRange(entries.Values); }
        internal void Clear()
        {
            entries.Clear(); snapshot = null; snapshotDirty = true;
            foreach (var buffer in buffers) if (buffer.Readers == 0) ClearBuffer(buffer);
        }
    }
}
