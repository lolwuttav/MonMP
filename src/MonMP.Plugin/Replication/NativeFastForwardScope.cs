using System;
using System.Collections;

namespace MonMP.Plugin.Replication
{
    // Only native hour/day fast-forward owns a shared needs reset. The standalone
    // refill cheat and coordinated sleep/death recovery own their recipients.
    internal static class NativeFastForwardScope
    {
        internal static bool Active { get; private set; }
        internal static IEnumerator Coordinate(IEnumerator native, int amount, SpecialMenu? nativeMenu = null)
        {
            var clock = MonMPPlugin.Instance?.World?.Clock;
            bool started = false, completed = false;
            try
            {
                started = true;
                clock?.BeginNativeFastForward(amount);
                while (true)
                {
                    if (clock?.NativeFastForwardAttached == false) yield break;
                    bool previous = Active, moved;
                    try { Active = true; moved = native.MoveNext(); }
                    finally { Active = previous; }
                    if (!moved) { completed = true; yield break; }
                    clock?.PublishNativeFastForward();
                    yield return native.Current;
                }
            }
            finally
            {
                try
                {
                    (native as IDisposable)?.Dispose();
                    // Native cancellation has no finally. Hide its menu overlay
                    // without invoking the day-skip Sleep side effect again.
                    if (started && !completed && nativeMenu != null) nativeMenu.FastForwardingTime_Completed(0);
                }
                finally { if (started) clock?.EndNativeFastForward(); }
            }
        }
    }
}
