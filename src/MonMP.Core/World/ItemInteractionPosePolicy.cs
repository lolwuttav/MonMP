using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // Native item and tool input remains available while using a mechanic creeper or
    // furniture chair. Vehicle seats and wheelbarrows have different input/hand rules.
    public static class ItemInteractionPosePolicy
    {
        public const byte CreeperState = 3;
        public const byte ChairState = 4;

        public static bool AllowsState(byte state) => state == PlayerPose.WalkingState ||
            state == CreeperState || state == ChairState;

        public static bool Allows(PlayerPose pose) => AllowsState(pose.State) &&
            pose.Rest == PlayerRest.None && !pose.Spectating;
    }
}
