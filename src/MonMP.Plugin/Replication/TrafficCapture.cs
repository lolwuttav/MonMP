using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal static class TrafficCapture
    {
        internal static TrafficSlotState Reuse(TrafficSlotState? previous, int slot, ulong fingerprint, bool active,
            ItemPose pose, float vx, float vy, float vz, TrafficLightFlags lights, float r, float g, float b, float a)
        {
            if (previous != null && previous.Slot == slot && previous.PrefabFingerprint == fingerprint && previous.Active == active &&
                Float(previous.Pose.X, pose.X) && Float(previous.Pose.Y, pose.Y) && Float(previous.Pose.Z, pose.Z) &&
                Float(previous.Pose.Qx, pose.Qx) && Float(previous.Pose.Qy, pose.Qy) && Float(previous.Pose.Qz, pose.Qz) && Float(previous.Pose.Qw, pose.Qw) &&
                Float(previous.VelocityX, vx) && Float(previous.VelocityY, vy) && Float(previous.VelocityZ, vz) &&
                previous.Lights == lights && Float(previous.ColorR, r) && Float(previous.ColorG, g) && Float(previous.ColorB, b) && Float(previous.ColorA, a)) return previous;
            return new TrafficSlotState(slot, fingerprint, active, pose, vx, vy, vz, lights, r, g, b, a);
        }
        private static bool Float(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }
}
