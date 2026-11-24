namespace MonMP.Core.World
{
    /// <summary>
    /// The vanilla syrup-barrel receivers mutate shared economy and consume the barrel.
    /// Until a receiver has a complete host-routed transaction, only offline play and the
    /// host may execute that native mutation. Host item snapshots still apply normally.
    /// </summary>
    public static class Barrel120AuthorityPolicy
    {
        public static bool MayRunNativeStoreReceive(bool multiplayerRunning, bool isHost) =>
            !multiplayerRunning || isHost;
    }
}
