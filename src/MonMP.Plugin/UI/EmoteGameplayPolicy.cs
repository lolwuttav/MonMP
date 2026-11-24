using MonMP.Core.Input;

namespace MonMP.Plugin.UI
{
    // Keep wheel display eligibility and native hand-action safety separate so seated and
    // carrying play can keep the wheel without invoking native actions in unsafe poses.
    internal static class EmoteGameplayPolicy
    {
        // Wheel visibility is deliberately broader than emote execution: active gameplay states
        // may show the wheel even when their native pose cannot safely play a selected emote.
        internal static bool IsWheelDisplayState(PlayerState state)
        {
            switch (state)
            {
                case PlayerState.Walking:
                case PlayerState.InVehicle_Driver:
                case PlayerState.InVehicle_Passenger:
                case PlayerState.InCreeper:
                case PlayerState.InFurnitureChair:
                case PlayerState.InWheelbarrow:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool CanPerformSelectedAction(EmoteWheelSlot slot, PlayerState state, bool handsFree)
        {
            bool nativeHandAction = slot == EmoteWheelSlot.Sorry || slot == EmoteWheelSlot.Spit ||
                slot == EmoteWheelSlot.Slur;
            if (!IsWheelDisplayState(state)) return false;
            if (!nativeHandAction) return true;
            return handsFree && IsNativeHandActionState(state);
        }

        internal const float PreviewViewerFacingYaw = 180f;

        internal static bool IsNativeHandActionState(PlayerState state)
        {
            switch (state)
            {
                case PlayerState.Walking:
                case PlayerState.InVehicle_Driver:
                case PlayerState.InVehicle_Passenger:
                case PlayerState.InFurnitureChair:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool VehicleControlsMayBeActive(PlayerState state) =>
            state == PlayerState.InVehicle_Driver;
    }
}
