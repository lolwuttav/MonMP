using System;
using System.Collections;
using MonMP.Plugin.Patches;

namespace MonMP.Plugin.Replication
{
    // A native door can refuse after a yield. Each step carries the original actor, while
    // messages outside that step retain their ordinary local presentation.
    internal sealed class StorageGarageMotionFeedback : IEnumerator, IDisposable
    {
        private readonly IEnumerator inner;
        private readonly Func<IDisposable> enterActor;
        private readonly Action<string> reply;
        internal StorageGarageMotionFeedback(IEnumerator inner, Func<IDisposable> enterActor, Action<string> reply)
        { this.inner = inner; this.enterActor = enterActor; this.reply = reply; }
        public object Current => inner.Current;
        public bool MoveNext()
        {
            string? previousError = RemoteActionScope.LastError;
            string? error = null;
            try
            {
                using (enterActor())
                using (RemoteActionScope.Enter())
                {
                    RemoteActionScope.LastError = null;
                    try { return inner.MoveNext(); }
                    finally { error = RemoteActionScope.LastError; }
                }
            }
            finally
            {
                RemoteActionScope.LastError = previousError;
                if (error != null) reply(error);
            }
        }
        public void Reset() => inner.Reset();
        public void Dispose() { if (inner is IDisposable disposable) disposable.Dispose(); }
    }
}
