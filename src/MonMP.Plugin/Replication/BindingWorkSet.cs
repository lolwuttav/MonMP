using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // A detached view for adapters whose callbacks can synchronously add or retire work.
    // Protocol tombstones stay in the main catalog; only live/retry work belongs here.
    internal sealed class BindingWorkSet<T> where T : class
    {
        private readonly HashSet<T> members = new HashSet<T>();
        private T[] snapshot = Array.Empty<T>();
        private bool dirty;

        internal int Count => members.Count;
        internal void Add(T value) { if (members.Add(value)) dirty = true; }
        internal void Remove(T value) { if (members.Remove(value)) dirty = true; }
        internal T[] Snapshot
        {
            get
            {
                if (dirty)
                {
                    snapshot = new T[members.Count];
                    members.CopyTo(snapshot);
                    dirty = false;
                }
                return snapshot;
            }
        }
        internal void Clear()
        {
            members.Clear(); snapshot = Array.Empty<T>(); dirty = false;
        }
    }
}
