using System;

namespace MonMP.Plugin.Replication
{
    // Display/held-tool prediction only. The host debits the leased pack for each accepted plant.
    internal sealed class SeedCountPrediction
    {
        private uint generation;
        private float lastAuthoritative, pending, until;
        internal bool HasPending => pending > 0;
        internal bool Expired(float now) => pending > 0 && now >= until;

        internal void Spend(float authoritative, uint leaseGeneration, float now)
        {
            if (generation != leaseGeneration || pending <= 0 || Expired(now))
            { generation = leaseGeneration; lastAuthoritative = authoritative; pending = 0; }
            pending = Math.Min(pending + 1, Math.Max(0, authoritative));
            until = now + 2.5f;
        }

        internal float Reconcile(float authoritative, uint leaseGeneration, float now, bool force)
        {
            if (force || generation != leaseGeneration || Expired(now) || authoritative > lastAuthoritative)
                pending = 0;
            else pending = Math.Max(0, pending - Math.Max(0, lastAuthoritative - authoritative));
            generation = leaseGeneration; lastAuthoritative = authoritative;
            return Math.Max(0, authoritative - pending);
        }
    }
}
