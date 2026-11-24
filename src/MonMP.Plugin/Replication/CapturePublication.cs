using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Immutable captures may skip serialization only while the channel still owns
    // the exact record proved to contain them. Direct/native action publications
    // invalidate this guard without waiting for another player or another cadence.
    internal sealed class CapturePublication
    {
        private object? state;
        private WorldRecord? record;
        internal bool CanSkip(object current, WorldRecord? currentRecord) =>
            ReferenceEquals(current, state) && record != null && ReferenceEquals(record, currentRecord);
        internal void Published(object current, WorldRecord? currentRecord, byte[] payload)
        {
            state = current;
            record = currentRecord?.SameValue(payload) == true ? currentRecord : null;
        }
    }
}
