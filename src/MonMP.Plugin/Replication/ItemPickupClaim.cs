using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed class ItemPickupClaim
    {
        internal const float Timeout = 3f, RetrySeconds = .5f;
        internal readonly uint Peer, Nonce, Generation;
        internal readonly float StartedAt;
        internal float NextCancel;
        internal bool Cancelled;
        internal ItemPickupClaim(uint peer, uint nonce, uint generation, float now)
        { Peer = peer; Nonce = nonce; Generation = generation; StartedAt = now; }
        internal uint GrantedGeneration => ItemPickupRequest.GrantedGeneration(Generation);
        internal bool Matches(ItemPickupReply reply) => reply.Nonce == Nonce && reply.Generation == Generation;
        internal bool Matches(uint peer, ItemPickupRequest request) => peer == Peer && request.Nonce == Nonce && request.Generation == Generation;
        internal bool Owns(ItemLease lease) => lease.Owner == Peer && lease.Generation == GrantedGeneration;
        internal bool Lost(ItemState state) => !state.Exists || (!Owns(state.Lease) &&
            (state.Lease.Owner != ItemLease.Unowned || state.Lease.Generation != Generation));
    }
}
