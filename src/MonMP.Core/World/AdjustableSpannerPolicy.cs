namespace MonMP.Core.World
{
    /// <summary>
    /// Native wrench input already has host authority for installed-bolt progress and shared
    /// loose-bolt ownership. The remaining direct vehicle-part mutation is the right-click
    /// ShifterKnob detach, which must not run on a guest replica.
    /// </summary>
    public static class AdjustableSpannerPolicy
    {
        public static bool BlocksGuestShifterKnobDetach(bool sessionRunning, bool isHost, bool hardThrow,
            bool holdingBolt, bool targetingBoltHole, bool targetingShifterKnob) =>
            sessionRunning && !isHost && hardThrow && !holdingBolt && !targetingBoltHole && targetingShifterKnob;
    }
}
