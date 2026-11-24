using System;
using System.Collections;

namespace MonMP.Plugin.Replication
{
    // Multiplayer death/pass-out never advances shared time. A guest's coroutine
    // may outlive its session: reject personal completion after its offline baseline
    // is restored, while allowing native respawn/presentation/control cleanup.
    internal static class GuestDeathTransition
    {
        internal static bool BlockWorldAdvance { get; private set; }
        internal static bool BlockPersonalCompletion { get; private set; }

        internal static IEnumerator Coordinate(IEnumerator native, SharedWorld origin, bool host = false)
        {
            try
            {
                while (true)
                {
                    bool previousWorld = BlockWorldAdvance, previousPersonal = BlockPersonalCompletion;
                    bool moved;
                    try
                    {
                        var plugin = MonMPPlugin.Instance;
                        BlockWorldAdvance = true;
                        BlockPersonalCompletion = previousPersonal || !host && (plugin?.World != origin ||
                            plugin.Session?.IsRunning != true || plugin.Session.IsHost);
                        moved = native.MoveNext();
                    }
                    finally
                    {
                        BlockWorldAdvance = previousWorld;
                        BlockPersonalCompletion = previousPersonal;
                    }
                    if (!moved) yield break;
                    // These fixed-update yields are exclusively the native time-jump
                    // loop. Its clock calls are suppressed; finish it immediately while
                    // retaining eye transitions, respawn, recovery and deliberate waits.
                    if (native.Current is UnityEngine.WaitForFixedUpdate) continue;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }
}
