using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Motion updates from all anglers share one bounded snapshot cadence.
    internal struct FishingTrashCosmeticBatch
    {
        private bool dirty;
        private float nextPublish;

        internal void Changed(ItemPose previous, ItemPose current)
        {
            if (previous.X != current.X || previous.Y != current.Y || previous.Z != current.Z ||
                previous.Qx != current.Qx || previous.Qy != current.Qy || previous.Qz != current.Qz || previous.Qw != current.Qw)
                dirty = true;
        }

        internal bool Due(float now) => dirty && now >= nextPublish;

        internal void Published(float now)
        {
            dirty = false;
            nextPublish = now + 0.25f;
        }
    }
}
