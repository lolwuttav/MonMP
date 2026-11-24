namespace MonMP.Core.World
{
    // Leo interaction callbacks mutate local-only native state. Until a validated
    // command can resolve the same car bed on every peer, a connected guest must
    // leave those callbacks to the host. Offline play and the host remain native.
    public static class LeoCarBedAuthorityPolicy
    {
        public static bool BlocksNativeCallback(bool sessionRunning, bool isHost) => sessionRunning && !isHost;
    }
}
