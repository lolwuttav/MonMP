using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        internal bool HostCanReceiveItem(uint peer) => host && session?.IsHost == true && Ready && !TransfersBlocked &&
            (peer == 0 ? !gameplay.Player.IsHoldingAnObject : session.Peers.ContainsKey(peer)) &&
            !PeerHolding(peer) && TryItemPlayerPosition(peer, out _);

        // Remote hand animation is presentation. Use accepted, fresh player presence for
        // the initial spawn; the leased item's normal hand presentation takes over next.
        internal bool TryGetPeerHandPose(uint peer, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (!host || !TryItemPlayerPosition(peer, out var player)) return false;
            if (peer == 0)
            {
                position = gameplay.Player.HandLocation.position;
                rotation = gameplay.Player.HandLocation.rotation;
            }
            else
            {
                rotation = Quaternion.Euler(0f, presence[peer].Pose.Yaw, 0f);
                position = player + Vector3.up * 1.2f + rotation * Vector3.forward * 0.5f;
            }
            return true;
        }
    }
}
