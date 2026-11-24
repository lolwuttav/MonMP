namespace MonMP.Core.World
{
    /// <summary>
    /// Maple syrup crate packing and sales, and syrup-tanker sales, mutate world cargo
    /// plus economy. Keep their vanilla code on the host until those triggers have a
    /// routed guest command; offline play and host play retain the native behavior.
    /// </summary>
    public static class MapleSyrupCargoAuthorityPolicy
    {
        public static bool MayRunNativeMutation(bool multiplayerRunning, bool isHost) =>
            !multiplayerRunning || isHost;
    }
}
