using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    // Host-side, session-only catch correlation. A trash result gets one short-lived item-spawn
    // authorization tied to the accepted rod owner, lease generation and catch ticket.
    public sealed class FishingCatchTicketBook
    {
        private readonly struct Key : IEquatable<Key>
        {
            public readonly ulong Rod;
            public readonly uint Generation, Ticket;
            public Key(ulong rod, uint generation, uint ticket) { Rod = rod; Generation = generation; Ticket = ticket; }
            public bool Equals(Key other) => Rod == other.Rod && Generation == other.Generation && Ticket == other.Ticket;
            public override bool Equals(object? obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Rod, Generation, Ticket);
        }
        private sealed class Authorization
        {
            public uint Owner;
            public ushort Template;
            public float X, Y, Z, ExpiresAt;
            public bool Consumed;
        }
        private readonly Dictionary<Key, Authorization> trash = new Dictionary<Key, Authorization>();
        private uint nextTicket;
        public int PendingTrashCount => trash.Count;

        // Repeated reports of the same caught phase keep the host's ticket. A second result cannot
        // be issued while the rod remains Caught; it must first report a non-caught phase.
        public bool TryAccept(FishingPresentationState report, FishingPresentationState? previous, float now,
            float trashRetentionSeconds, out FishingPresentationState accepted)
        {
            accepted = default;
            if (report.CatchTicket != 0 || !float.IsFinite(now) || !float.IsFinite(trashRetentionSeconds) ||
                trashRetentionSeconds <= 0 || trashRetentionSeconds > 60)
                return false;
            if (report.Phase != FishingPresentationPhase.Caught)
            { accepted = report; return true; }
            if (report.Outcome == FishingCatchOutcome.None ||
                (report.Outcome == FishingCatchOutcome.Trash ? report.TrashTemplate == FishingPresentationState.NoTrashTemplate : report.TrashTemplate != FishingPresentationState.NoTrashTemplate))
                return false;
            if (previous.HasValue && previous.Value.Rod == report.Rod && previous.Value.Owner == report.Owner &&
                previous.Value.Generation == report.Generation && previous.Value.Phase == FishingPresentationPhase.Caught)
            {
                if (previous.Value.Outcome != report.Outcome || previous.Value.TrashTemplate != report.TrashTemplate || previous.Value.CatchTicket == 0)
                    return false;
                accepted = report.WithCatchTicket(previous.Value.CatchTicket);
                return true;
            }

            uint ticket = NextTicket();
            accepted = report.WithCatchTicket(ticket);
            if (report.Outcome == FishingCatchOutcome.Trash)
            {
                var key = new Key(report.Rod, report.Generation, ticket);
                trash[key] = new Authorization
                { Owner = report.Owner, Template = report.TrashTemplate, X = report.X, Y = report.Y, Z = report.Z, ExpiresAt = now + trashRetentionSeconds };
                Prune(now);
                if (trash.Count > 128)
                    foreach (var stale in trash.OrderBy(p => p.Value.ExpiresAt).Take(trash.Count - 128).Select(p => p.Key).ToArray()) trash.Remove(stale);
            }
            return true;
        }

        public bool CanConsume(ulong rod, uint generation, uint ticket, uint owner, ushort template,
            float x, float y, float z, float now, float maxDistance)
        {
            if (rod == 0 || generation == 0 || ticket == 0 || owner == ItemLease.Unowned || template == FishingPresentationState.NoTrashTemplate ||
                !float.IsFinite(now) || !float.IsFinite(maxDistance) || maxDistance <= 0 || maxDistance > 100 ||
                !ItemPose.FiniteValue(x, 100000) || !ItemPose.FiniteValue(y, 100000) || !ItemPose.FiniteValue(z, 100000) ||
                !trash.TryGetValue(new Key(rod, generation, ticket), out var auth) || auth.Owner != owner || auth.Template != template || auth.Consumed || auth.ExpiresAt <= now)
                return false;
            float dx = x - auth.X, dy = y - auth.Y, dz = z - auth.Z;
            return dx * dx + dy * dy + dz * dz <= maxDistance * maxDistance;
        }

        public bool Consume(ulong rod, uint generation, uint ticket, uint owner, ushort template, float now)
        {
            if (!trash.TryGetValue(new Key(rod, generation, ticket), out var auth) || auth.Owner != owner || auth.Template != template ||
                auth.Consumed || auth.ExpiresAt <= now) return false;
            auth.Consumed = true; return true;
        }

        public void RemoveOwner(uint owner)
        { foreach (var key in trash.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) trash.Remove(key); }
        public void Prune(float now)
        { foreach (var key in trash.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) trash.Remove(key); }

        private uint NextTicket()
        {
            do { nextTicket++; if (nextTicket == 0) nextTicket++; }
            while (trash.Keys.Any(k => k.Ticket == nextTicket));
            return nextTicket;
        }
    }
}
