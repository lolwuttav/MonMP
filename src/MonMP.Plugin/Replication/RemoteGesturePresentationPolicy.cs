using MonMP.Plugin.UI;

namespace MonMP.Plugin.Replication
{
    // Cosmetic hand gestures can be shown in the native lying and pushing poses too. Their
    // higher-priority held/push goals still win in StockAvatarBody; native voice/hand actions
    // continue to use the narrower supported state list.
    internal static class RemoteGesturePresentationPolicy
    {
        internal static bool IsAvailable(PlayerState state) => EmoteGameplayPolicy.IsWheelDisplayState(state);

        internal static bool CanPresent(PlayerState state, bool standing, bool hasSeatOrChairFrame) =>
            IsAvailable(state) && (state == PlayerState.InCreeper || state == PlayerState.InWheelbarrow ||
                standing || (state != PlayerState.Walking && hasSeatOrChairFrame));
    }
}
