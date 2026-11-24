using System;
using System.Collections;

namespace MonMP.Plugin.Replication
{
    internal static class MultiplayerPermadeath
    {
        internal static IEnumerator Coordinate(IEnumerator native, SharedWorld origin, bool host)
        {
            try
            {
                var plugin = MonMPPlugin.Instance;
                // The iterator can first run after a disconnect or replacement session.
                if (plugin?.World != origin || plugin.Session?.IsRunning != true || plugin.Session.IsHost != host) yield break;
                if (!host)
                {
                    // Never enter native PermaDeath on a guest: it deletes the selected
                    // save and leaves a terminal overlay rather than a spectator camera.
                    plugin.BeginGuestPermadeath();
                    yield break;
                }
                // Finish all save/journal cleanup before native deletion. No active
                // coordinator may recreate the deleted world on its next update.
                var stillOwnsSave = plugin.EndHostPermadeath(origin);
                // Let a reentrant save/update unwind before native deletion. Its
                // terminal coordinator cannot publish or journal further writes.
                yield return null;
                if (!stillOwnsSave()) yield break;
                while (native.MoveNext()) yield return native.Current;
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }
}
