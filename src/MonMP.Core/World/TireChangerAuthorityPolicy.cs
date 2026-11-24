namespace MonMP.Core.World
{
    // A peer may ask the host to perform a tire-changer interaction only when the world,
    // actor, machine and (where applicable) leased item all pass the same host-side checks.
    public static class TireChangerAuthorityPolicy
    {
        public static bool AllowNativeMutation(bool multiplayerRunning, bool isHost) =>
            !multiplayerRunning || isHost;

        public static bool AllowGuestRequest(bool multiplayerRunning, bool isHost, bool worldReady,
            bool saveBlocked, bool walking, bool poseFresh, bool inRange,
            bool machineIdle, bool handsFree, bool itemLeaseValid, bool itemValid, bool atTarget) =>
            multiplayerRunning && !isHost && worldReady && !saveBlocked && walking &&
            poseFresh && inRange && machineIdle && handsFree && itemLeaseValid && itemValid && atTarget;

        public static bool ValidWheelMount(bool machineIdle, bool machineEmpty, bool itemOwned,
            bool wheelTypeMatchesStation, bool atTarget) =>
            machineIdle && machineEmpty && itemOwned && wheelTypeMatchesStation && atTarget;

        public static bool ValidTireMount(bool machineIdle, bool rimWheelPresent, bool itemOwned,
            bool itemIsTire, bool atTarget) =>
            machineIdle && rimWheelPresent && itemOwned && itemIsTire && atTarget;

        public static bool ValidCycleStart(bool machineIdle, bool derimWheelReady,
            bool freeHands, bool pressureAllowed) =>
            machineIdle && freeHands && pressureAllowed && derimWheelReady;

        public static bool ValidWheelRemoval(bool machineIdle, bool wheelPresent, bool guestHandsFree,
            bool itemUnowned) => machineIdle && wheelPresent && guestHandsFree && itemUnowned;
    }
}
