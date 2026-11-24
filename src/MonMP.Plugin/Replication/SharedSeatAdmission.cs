using System;
using System.Collections.Generic;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // World commands run serially on the Unity thread. Consult every seat registry immediately
    // before mutating one, so an accepted claim is visible to the next adapter's command.
    internal sealed class SharedSeatAdmission
    {
        private const float PendingTimeout = 4f;
        private readonly Func<float> now;
        private readonly Func<uint, bool>[] owners;
        private object? pendingAdapter;
        private ulong pendingEntity;
        private uint pendingGeneration;
        private float pendingUntil;
        // One tombstone per known adapter/seat. Keep it until authority advances
        // beyond the canceled grant, so even a very late delta cannot sit us down.
        private readonly Dictionary<(object Adapter, ulong Entity), uint> canceled = new Dictionary<(object, ulong), uint>();
        // Tombstones left only by the silent pending timeout. There is no pending panel
        // (failure-only pop-ups), so a slow grant for a player still waiting at that
        // seat is wanted; one who walked away, sat elsewhere or picked something up is not.
        private readonly HashSet<(object Adapter, ulong Entity)> expired = new HashSet<(object, ulong)>();

        internal SharedSeatAdmission(Func<float> now, params Func<uint, bool>[] owners)
        {
            this.now = now;
            this.owners = owners;
        }

        internal bool IsSeated(uint peer)
        {
            // The shared vacancy sentinel is never a player, including during construction.
            if (peer == SeatOccupancy.Unoccupied) return true;
            foreach (var owns in owners)
                if (owns(peer)) return true;
            return false;
        }

        internal bool TryBeginLocal(object adapter, ulong entity, uint generation, uint peer)
        {
            Expire();
            if (IsSeated(peer)) return false;
            if (pendingAdapter != null)
                // Retrying the same claim is safe; a second seat must await its outcome.
                return ReferenceEquals(pendingAdapter, adapter) && pendingEntity == entity && pendingGeneration == generation;
            pendingAdapter = adapter;
            pendingEntity = entity;
            pendingGeneration = generation;
            pendingUntil = now() + PendingTimeout;
            // A deliberate new click on the same still-vacant generation wants
            // this seat again; an indistinguishable older grant now has that goal.
            canceled.Remove((adapter, entity)); expired.Remove((adapter, entity));
            return true;
        }

        internal bool IsPendingLocal(object adapter, ulong entity)
        {
            Expire();
            return ReferenceEquals(pendingAdapter, adapter) && pendingEntity == entity;
        }

        // stillWanted: the player is still free and standing at this seat. It overrides only a
        // timeout tombstone, never a deliberate cancellation (save, settings, disposal).
        internal bool AllowsLocalEntry(object adapter, ulong entity, uint generation, bool stillWanted = false)
        {
            Expire();
            return !canceled.TryGetValue((adapter, entity), out uint refused) || refused != generation ||
                stillWanted && expired.Contains((adapter, entity));
        }

        // The host turned this exact claim down. End it without a tombstone: nothing was
        // granted, and a later deliberate click may claim the same generation again.
        internal bool Refused(ulong entity, uint generation)
        {
            Expire();
            if (pendingAdapter == null || pendingEntity != entity || pendingGeneration != generation) return false;
            pendingAdapter = null;
            return true;
        }

        internal void CompleteLocal(object adapter, ulong entity)
        {
            Expire();
            if (ReferenceEquals(pendingAdapter, adapter) && pendingEntity == entity) pendingAdapter = null;
        }

        internal void Observe(object adapter, ulong entity, uint generation)
        {
            Expire();
            if (canceled.TryGetValue((adapter, entity), out uint refused) && unchecked((int)(generation - refused)) > 0)
            { canceled.Remove((adapter, entity)); expired.Remove((adapter, entity)); }
            // An unchanged pre-grant snapshot must not release a pending entry.
            if (ReferenceEquals(pendingAdapter, adapter) && pendingEntity == entity && pendingGeneration != generation)
                pendingAdapter = null;
        }

        internal void CancelLocal(object adapter)
        {
            if (!ReferenceEquals(pendingAdapter, adapter)) return;
            canceled[(adapter, pendingEntity)] = pendingGeneration == uint.MaxValue ? 1 : pendingGeneration + 1;
            expired.Remove((adapter, pendingEntity));
            pendingAdapter = null;
        }

        private void Expire()
        {
            if (pendingAdapter == null || now() < pendingUntil) return;
            var key = (pendingAdapter, pendingEntity);
            CancelLocal(pendingAdapter);
            expired.Add(key);
        }
    }
}
