using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native wheel scans do not honor Physics.IgnoreCollision. Hide only this
    // vehicle's pinned shapes for the synchronous scan, then restore every layer.
    internal sealed class FrozenCargoWheelQuery : IDisposable
    {
        private static readonly Stack<FrozenCargoWheelQuery> pool = new Stack<FrozenCargoWheelQuery>();
        private readonly List<(GameObject Object, int Layer)> changed = new List<(GameObject, int)>();
        private bool disposed, eligible;
        internal static FrozenCargoWheelQuery Rent()
        {
            var scope = pool.Count != 0 ? pool.Pop() : new FrozenCargoWheelQuery();
            scope.disposed = false; return scope;
        }
        internal bool Any => eligible;
        internal void Hide(Collider shape)
        {
            if (shape == null || !shape.enabled || shape.isTrigger) return;
            eligible = true;
            if (shape.gameObject.layer == 2) return;
            changed.Add((shape.gameObject, shape.gameObject.layer)); shape.gameObject.layer = 2;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in changed) if (entry.Object != null) entry.Object.layer = entry.Layer;
            changed.Clear(); eligible = false;
            if (pool.Count < 8) pool.Push(this);
        }
    }
}
