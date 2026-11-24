namespace MonMP.Plugin.Replication
{
    // Only the current world's owner receives lifecycle events. An older world's
    // cleanup cannot clear a newer owner; no references survive Dispose.
    internal static class NativeItemDiscovery
    {
        private static SharedItems? owner;
        internal static void Begin(SharedItems items) => owner = items;
        internal static void End(SharedItems items) { if (ReferenceEquals(owner, items)) owner = null; }
        internal static void Changed(InteractableItems item) => owner?.ItemChanged(item);
        internal static void Destroyed(InteractableItems item) => owner?.ItemDestroyed(item);
    }
}
