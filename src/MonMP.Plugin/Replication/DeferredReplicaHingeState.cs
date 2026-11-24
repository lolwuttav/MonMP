namespace MonMP.Plugin.Replication
{
    // A dormant chassis must retain its native hinge shape until the motion adapter validates it.
    // Keep only the latest accepted presentation; waking a replica does not replay historical cues.
    internal sealed class DeferredReplicaHingeState<T>
    {
        private T pending = default!;
        private bool hasPending;

        internal bool Accept(T state, bool ready)
        {
            hasPending = !ready;
            if (!ready) pending = state;
            return ready;
        }

        internal bool TryTake(bool ready, out T state)
        {
            state = pending;
            if (!ready || !hasPending) return false;
            hasPending = false;
            pending = default!;
            return true;
        }
    }
}
