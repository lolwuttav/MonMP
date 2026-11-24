using MonMP.Core.Protocol;

namespace MonMP.Plugin.Replication
{
    internal static class SpectatorPoseBuilder
    {
        // Native dead/seat/hand state remains untouched. Spectator packets use
        // the existing head-only wire contract while keeping the real body root.
        internal static PlayerPose FromNative(in PlayerPose pose, float headX, float headY, float headZ) =>
            new PlayerPose(pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch, PlayerPose.WalkingState,
                shirt: pose.Shirt, emoteSequence: pose.EmoteSequence, drunk: pose.Drunk, hungover: pose.Hungover,
                spectating: true, spectatorHeadX: headX, spectatorHeadY: headY, spectatorHeadZ: headZ,
                recentAction: pose.RecentAction, model: pose.Model, appearance: pose.Appearance);
    }
}
