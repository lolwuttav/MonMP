using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Preserve immutable state/record identities for unchanged host captures. An action
    // replacing the channel record invalidates the shortcut even if Binding.State was
    // refreshed before Publish. No serialized bytes are borrowed or mutated.
    internal static class ItemStatePublication
    {
        internal static ItemState Reuse(ItemState? previous, ushort prefab, bool exists, ItemLease lease,
            ItemPose pose, float scaleX = 1, float scaleY = 1, float scaleZ = 1,
            ConsumptionReceipt? consumption = null, ItemValues values = default,
            ulong containerEntity = 0, byte containerSlot = byte.MaxValue, ulong carrier = 0)
        {
            if (previous != null && Matches(previous, prefab, exists, lease, pose, scaleX, scaleY, scaleZ,
                consumption, values, containerEntity, containerSlot, carrier)) return previous;
            return new ItemState(prefab, exists, lease, pose, scaleX, scaleY, scaleZ,
                consumption, values, containerEntity, containerSlot, carrier);
        }

        internal static bool CanSkip(ItemState state, ItemState? published, WorldRecord? publishedRecord, WorldRecord? currentRecord) =>
            published != null && publishedRecord != null && ReferenceEquals(publishedRecord, currentRecord) &&
            Matches(published, state.Prefab, state.Exists, state.Lease, state.Pose, state.ScaleX, state.ScaleY, state.ScaleZ,
                state.Consumption, state.Values, state.ContainerEntity, state.ContainerSlot, state.Carrier);

        private static bool Matches(ItemState state, ushort prefab, bool exists, ItemLease lease, ItemPose pose,
            float scaleX, float scaleY, float scaleZ, ConsumptionReceipt? consumption, ItemValues values,
            ulong containerEntity, byte containerSlot, ulong carrier) =>
            state.Prefab == prefab && state.Exists == exists && state.Lease.Owner == lease.Owner && state.Lease.Generation == lease.Generation &&
            state.Pose.X == pose.X && state.Pose.Y == pose.Y && state.Pose.Z == pose.Z &&
            state.Pose.Qx == pose.Qx && state.Pose.Qy == pose.Qy && state.Pose.Qz == pose.Qz && state.Pose.Qw == pose.Qw &&
            state.ScaleX == scaleX && state.ScaleY == scaleY && state.ScaleZ == scaleZ &&
            ReferenceEquals(state.Consumption, consumption) &&
            state.Values.Value1 == values.Value1 && state.Values.Value2 == values.Value2 &&
            state.Values.IntValue1 == values.IntValue1 && state.Values.IntValue2 == values.IntValue2 && state.Values.Flags == values.Flags &&
            state.ContainerEntity == containerEntity && state.ContainerSlot == containerSlot && state.Carrier == carrier;
    }
}
