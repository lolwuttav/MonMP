namespace MonMP.Core.World
{
    // The torch's flame, sound and fuel are the holder's own presentation. What it ignites is
    // shared: a guest may run the native torch only while shared paper ignition is host-routed.
    public static class MapTorchAuthorityPolicy
    {
        public static bool AllowNativeMutation(bool multiplayerRunning, bool isHost, bool ignitionRouted) =>
            !multiplayerRunning || isHost || ignitionRouted;
    }
}
