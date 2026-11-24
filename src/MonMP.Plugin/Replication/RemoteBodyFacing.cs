using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteBodyFacing
    {
        internal const float StandingSlack = 45f;
        internal const float IdleRecoveryDegrees = 90f;
        internal static float Offset(float previous, float cameraTurn, bool standing, bool walking, bool furnitureHeld, float deltaTime)
        {
            // The camera already has network interpolation. A large object held
            // with both hands should not add the idle body's multi-second turn lag.
            if (!standing || furnitureHeld) return 0;
            return Mathf.MoveTowards(Mathf.Clamp(previous - cameraTurn, -StandingSlack, StandingSlack),
                0, deltaTime * (walking ? 360f : IdleRecoveryDegrees));
        }
    }
}
