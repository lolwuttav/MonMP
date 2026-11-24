namespace MonMP.Core.World
{
    /// <summary>
    /// The air-impact gun shares the stock wrench's direct ShifterKnob detach helper. Its
    /// installed-bolt progress may also target vehicle holes outside the replicated fleet.
    /// </summary>
    public static class AirImpactAuthorityPolicy
    {
        public static bool BlocksGuestShifterKnobDetach(bool sessionRunning, bool isHost, bool hardThrow,
            bool holdingBolt, bool targetingBoltHole, bool targetingShifterKnob) =>
            sessionRunning && !isHost && hardThrow && !holdingBolt && !targetingBoltHole && targetingShifterKnob;

        public static bool BlocksGuestUnsharedInstalledBoltProgress(bool sessionRunning, bool isHost,
            bool installedBoltTarget, bool sharedBoltTarget) =>
            sessionRunning && !isHost && installedBoltTarget && !sharedBoltTarget;
    }
}
