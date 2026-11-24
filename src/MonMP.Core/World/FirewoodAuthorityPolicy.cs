namespace MonMP.Core.World
{
    // Firewood remains a normal SharedItems entity, but burning and delivery also
    // invoke native world/profile mutations that must only run on the host.
    public static class FirewoodAuthorityPolicy
    {
        public static bool AllowNativeMutation(bool multiplayerRunning, bool isHost) =>
            !multiplayerRunning || isHost;
    }
}
