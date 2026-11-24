using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        // A posted fine is a physical letter, so it need not remain the active tool.
        // It may already be loose, or its reliable Drop may still be awaiting its reply.
        internal bool TryGetLocalPoliceEnvelope(Tool_Envelope envelope, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || disposed || session?.IsRunning != true || !Ready || TransfersBlocked ||
                envelope == null || !IsPoliceEnvelope(envelope) ||
                !byItem.TryGetValue(envelope, out var binding) || !binding.State.Exists || binding.Absorbed ||
                binding.PendingGrab != GrabMode.None) return false;
            var lease = binding.State.Lease;
            if (lease.Generation == 0 || (lease.Owner != ItemLease.Unowned && lease.Owner != session.LocalPeerId)) return false;
            id = binding.Id; generation = lease.Generation;
            // Drop was queued first on this same reliable channel. Its host release advances
            // the generation before the following signature/post request is processed.
            if (binding.Releasing && lease.Owner == session.LocalPeerId)
                generation = generation == uint.MaxValue ? 1 : generation + 1;
            return true;
        }

        internal bool HostAuthorizePoliceEnvelope(uint peer, ulong id, uint generation, out Tool_Envelope? envelope)
        {
            envelope = null;
            if (!host || disposed || session?.IsHost != true || peer == 0 || generation == 0 ||
                !bindings.TryGetValue(id, out var binding) || !binding.State.Exists || binding.Absorbed ||
                binding.Item is not Tool_Envelope candidate || !IsPoliceEnvelope(candidate)) return false;
            var lease = leases[id];
            if (lease.Generation != generation || (lease.Owner != peer && lease.Owner != ItemLease.Unowned)) return false;
            envelope = candidate;
            return true;
        }

        private static bool IsPoliceEnvelope(Tool_Envelope envelope) =>
            envelope is Envelope_Police_Ticket && envelope.envelope_Type == Envelope_Type.Police_Ticket;
    }
}
