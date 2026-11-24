using System;
using System.Collections;

namespace MonMP.Plugin.Replication
{
    // Native Load CD yields before it starts per-file coroutines. Scope each
    // MoveNext, rather than a button prefix or one frame-wide global flag.
    internal sealed class CdManualLoadScope<T> : IEnumerator, IDisposable
    {
        [ThreadStatic] internal static Action<T>? CurrentTrackLoaded;
        private readonly IEnumerator inner;
        private readonly Action<T>? trackLoaded;
        private readonly Action? afterMove;
        internal CdManualLoadScope(IEnumerator inner, Action<T>? trackLoaded, Action? afterMove = null)
        { this.inner = inner; this.trackLoaded = trackLoaded; this.afterMove = afterMove; }
        public object? Current => inner.Current;
        public bool MoveNext()
        {
            var previous = CurrentTrackLoaded;
            CurrentTrackLoaded = trackLoaded;
            try { return inner.MoveNext(); }
            finally { CurrentTrackLoaded = previous; afterMove?.Invoke(); }
        }
        public void Reset() => inner.Reset();
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
}
