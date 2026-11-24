namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        // An activated guest retains its loader to protect the borrowed world's
        // saves. That lifetime is not a pending join or an Appearance UI lock.
        internal bool MenuConnecting => GuestWorld != null && (!GuestWorld.Activated || GuestWorld.Cancelled) ||
            Session?.IsRunning == true && (!Session.IsReady || steamTransport != null && steamSessions?.Ready != true);
    }
}
