using System;

namespace MonMP.Plugin.Integration
{
    // Controller seat exit for a VR provider. It performs nothing itself: each source is one of
    // MonMP's seat adapters and its Click is the same re-click of the occupied seat a desktop
    // player makes, so native door checks, seat ownership, leases and host exit requests all run
    // exactly as they do for a mouse click. There is no forced dismount, no walker enabling and
    // no lease release here.
    internal static class LocalVrSeatExit
    {
        internal readonly struct Source
        {
            internal readonly string Name;
            // The local player occupies a seat this adapter owns and can re-click.
            internal readonly Func<bool> Seated;
            internal readonly Action Click;
            internal Source(string name, Func<bool> seated, Action click) { Name = name; Seated = seated; Click = click; }
        }

        // First matching seat; -1 proves none, -2 means a lookup failed and absence is unknown.
        internal static int Find(Source[] sources, Action<string> warn)
        {
            bool failed = false;
            for (int i = 0; i < sources.Length; i++)
            {
                try { if (sources[i].Seated()) return i; }
                catch (Exception ex) { failed = true; warn("MonMP local VR bridge: seat lookup '" + sources[i].Name + "' failed: " + ex.Message); }
            }
            return failed ? -2 : -1;
        }

        internal static bool Seated(bool providerOwnsView, Source[] sources, Action<string> warn) =>
            providerOwnsView && Find(sources, warn) >= 0;

        // False: MonMP has no seat to handle and the caller may use the native seat itself.
        // True: MonMP handled the request or refused it; the caller must not start a second
        // transition. A refusal leaves the player seated (closed door, native drink refusal,
        // pause/menu/UI/death/sleep/spectating restriction, or a failing adapter).
        internal static bool TryExit(bool providerOwnsView, Func<bool> restricted, Source[] sources, Action<string> warn)
        {
            if (!providerOwnsView) return false;
            int index = Find(sources, warn);
            if (index < 0) return index == -2; // Uncertain lookup is a refusal, never native fallback.
            bool blocked;
            try { blocked = restricted(); }
            catch (Exception ex) { warn("MonMP local VR bridge: seat exit gate failed: " + ex.Message); blocked = true; }
            if (blocked) return true;
            try { sources[index].Click(); }
            catch (Exception ex) { warn("MonMP local VR bridge: seat exit '" + sources[index].Name + "' failed: " + ex.Message); }
            return true;
        }
    }
}
