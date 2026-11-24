using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // State records may coalesce a complete open/close cycle. Each successful native host
    // sound call instead supplies its own live cue; historical snapshots never enter Play.
    internal static class VehicleDoorPresentation
    {
        private const byte Open = 0, Close = 1;
        private const int PendingLimit = 64;
        private const float PendingLifetime = 1f;
        private readonly struct Pending
        {
            internal readonly PresentationEvent Event;
            internal readonly float Deadline;
            internal Pending(PresentationEvent e) { Event = e; Deadline = Time.unscaledTime + PendingLifetime; }
        }
        private readonly struct NativePending
        {
            internal readonly SOUND_Vehicle_Door Sound;
            internal readonly byte Variant;
            internal readonly Vector3 Position;
            internal readonly float Deadline;
            internal NativePending(SOUND_Vehicle_Door sound, byte variant)
            { Sound = sound; Variant = variant; Position = sound.transform.position; Deadline = Time.unscaledTime + PendingLifetime; }
        }
        private static readonly List<Pending> cuePending = new List<Pending>();
        private static readonly List<NativePending> nativePending = new List<NativePending>();

#if MONMP_DEVELOPMENT
        internal static int TestPosted { get; private set; }


        internal static int TestPlayed { get; private set; }


        internal static int TestQueued { get; private set; }


        internal static int TestExpired { get; private set; }


        internal static int TestNativeQueued { get; private set; }


        internal static int TestNativeExpired { get; private set; }
#endif


        internal static void Native(SOUND_Vehicle_Door sound, bool open)
        {
            var plugin = MonMPPlugin.Instance;
            if (SharedPresentationEvents.Replaying || plugin?.Session?.IsRunning != true || !plugin.Session.IsHost ||
                PresentationReplays.Events?.Live != true || plugin.World == null || sound == null) return;
            Tick();
            var pending = new NativePending(sound, open ? Open : Close);
            if (!nativePending.Any(p => ReferenceEquals(p.Sound, sound)) && TryPost(pending)) return;
            // Instantiate may invoke a usable native door before the sale callback identifies its
            // clone. Retain only the actual sound component and captured call position; unknown
            // sources never publish and expire after one second under this fixed capacity.
            if (nativePending.Count >= PendingLimit) return;
            nativePending.Add(pending);
#if MONMP_DEVELOPMENT
TestNativeQueued++;
#endif

        }

        private static bool TryPost(NativePending pending)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsHost != true || pending.Sound == null || plugin.World == null ||
                !plugin.World.Vehicles.Doors.TryCueSource(pending.Sound, out ulong id)) return false;
            PresentationReplays.Post(PresentationKind.VehicleDoor, pending.Variant, id, pending.Position);

#if MONMP_DEVELOPMENT
            TestPosted++;
#endif
 return true;
        }

        internal static void Play(PresentationEvent e)
        {
            var plugin = MonMPPlugin.Instance;
            if (e.Kind != (byte)PresentationKind.VehicleDoor || (e.Variant != Open && e.Variant != Close) ||
                e.Actor != 0 || e.Value != 0 || plugin?.Session?.IsRunning != true || plugin.Session.IsHost || plugin.World == null) return;
            Tick();
            // Preserve same-door order if an earlier live cue awaits this replica.
            if (!cuePending.Any(p => p.Event.Target == e.Target) && TryPlay(e)) return;
            if (!plugin.World.Vehicles.Doors.IsCatalogPurchasedDoorCue(e.Target) || cuePending.Count >= PendingLimit) return;
            cuePending.Add(new Pending(e));
#if MONMP_DEVELOPMENT
TestQueued++;
#endif

        }

        private static bool TryPlay(PresentationEvent e)
        {
            var doors = MonMPPlugin.Instance?.World?.Vehicles.Doors;
            if (doors == null || !doors.TryCueTarget(e.Target, out var sound)) return false;
            // Native methods retain the native clip, volume, pitch and 30m transform culling.
            // Guest-only replay cannot post another cue, including delayed retries outside the bus scope.
            if (e.Variant == Open) sound.Open(); else sound.Close();

#if MONMP_DEVELOPMENT
            TestPlayed++;
#endif
 return true;
        }

        internal static void Tick()
        {
            if (cuePending.Count == 0 && nativePending.Count == 0) return;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || PresentationReplays.Events?.Live != true)
            { cuePending.Clear(); nativePending.Clear(); return; }
            if (plugin.Session.IsHost)
            {
                // The component reference preserves same-door call order even before an id exists.
                var blockedNative = new HashSet<SOUND_Vehicle_Door>();
                for (int i = 0; i < nativePending.Count;)
                {
                    var pending = nativePending[i];
                    if (Time.unscaledTime >= pending.Deadline)
                    { nativePending.RemoveAt(i);
#if MONMP_DEVELOPMENT
TestNativeExpired++;
#endif
 continue; }
                    if (!blockedNative.Contains(pending.Sound) && TryPost(pending))
                    { nativePending.RemoveAt(i); continue; }
                    blockedNative.Add(pending.Sound); i++;
                }
                return;
            }
            var blocked = new HashSet<ulong>();
            for (int i = 0; i < cuePending.Count;)
            {
                var pending = cuePending[i];
                if (Time.unscaledTime >= pending.Deadline)
                { cuePending.RemoveAt(i);
#if MONMP_DEVELOPMENT
TestExpired++;
#endif
 continue; }
                if (!blocked.Contains(pending.Event.Target) && TryPlay(pending.Event))
                { cuePending.RemoveAt(i); continue; }
                blocked.Add(pending.Event.Target); i++;
            }
        }
        internal static void Reset()
        {
            cuePending.Clear(); nativePending.Clear();

#if MONMP_DEVELOPMENT
            TestPosted = TestPlayed = TestQueued = TestExpired = TestNativeQueued = TestNativeExpired = 0;
#endif

        }
    }
}
