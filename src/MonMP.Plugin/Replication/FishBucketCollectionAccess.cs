using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal static class FishBucketCollectionAccess
    {
        internal static bool Allowed(uint peer, uint bucketOwner, uint fishOwner, bool touching,
            bool nearBucket, bool nearFish, bool host)
        {
            // A grounded bucket collects on contact without acquiring a hand lease. The host's
            // native contact may also collect a guest's rod catch; guests may only submit their
            // own fish or loose fish, and must be close to both objects.
            bool contact = bucketOwner == ItemLease.Unowned && touching;
            bool hostContact = host && peer == 0 && contact;
            return (bucketOwner == peer || contact) &&
                (hostContact || (nearBucket && nearFish)) &&
                (hostContact || fishOwner == ItemLease.Unowned || fishOwner == peer);
        }
    }
}
