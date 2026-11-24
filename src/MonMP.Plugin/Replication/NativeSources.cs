using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // World-scoped bookkeeping shared by every tracked native component type.
    internal static class NativeSourceTracking
    {
        // A tracked type is complete without a registry walk: the seeding walk includes
        // inactive objects and native Start enrolls everything created afterwards. The walk
        // that remains is insurance against a missed Start (an exception inside it, or a
        // component added by another mod's code path).
        internal const float Fallback = 30f;
        private static readonly List<Action> resets = new List<Action>();
        internal static Action<string>? Log;
        internal static void Register(Action reset) => resets.Add(reset);
        // A new or ended world forgets every remembered component. The next search of each
        // type is a complete registry walk again, whatever its caller's cadence.
        internal static void Reset() { foreach (var reset in resets) reset(); }
    }

    // The native components of one type that a recurring recovery search examines.
    //
    // FindObjectsOfType costs several milliseconds on a played save whatever the number of
    // matches, and nine adapters repeated it every one to three seconds. For a tracked type
    // the recurring search reads this remembered set instead: seeded by one complete walk,
    // extended by the type's native Start (or equivalent) hook, and reconciled by a complete
    // walk every Fallback seconds. Initial and forced searches always walk.
    //
    // The returned array has the contents FindObjectsOfType would return: destroyed objects
    // are dropped and, unless includeInactive, objects on inactive GameObjects are left out.
    // An untracked type keeps the plain native query.
    internal static class NativeSources<T> where T : MonoBehaviour
    {
        private static readonly List<T> known = new List<T>();
        private static readonly HashSet<T> index = new HashSet<T>();
        private static bool tracked, seeded, reported;
        private static float nextWalk, lastWalk, fallback = NativeSourceTracking.Fallback;

        internal static bool Tracked => tracked;
        internal static int KnownCount => known.Count;

        internal static void Track(float fallbackSeconds = NativeSourceTracking.Fallback)
        {
            fallback = fallbackSeconds;
            if (tracked) return;
            tracked = true;
            NativeSourceTracking.Register(Reset);
        }

        internal static void Reset()
        {
            known.Clear(); index.Clear();
            seeded = false; reported = false; nextWalk = 0; lastWalk = 0;
        }

        // Called after the type's native initialization. Before the seeding walk there is
        // nothing to extend: that walk will find the object itself.
        internal static void Started(T instance)
        {
            if (!seeded || instance == null) return;
            // The native query never returns DontSave objects; do not make them candidates.
            if ((instance.hideFlags & HideFlags.DontSave) != 0) return;
            if (index.Add(instance)) known.Add(instance);
        }

        // True when the next Find(periodic) performs a native registry walk and therefore
        // needs a scan admission. A remembered pass is managed work and needs none.
        internal static bool WalkDue(bool periodic)
        {
            if (!tracked || !periodic || !seeded) return true;
            float now = Time.unscaledTime;
            // A restarted clock must not postpone the reconciliation indefinitely.
            return now >= nextWalk || now < lastWalk;
        }

        internal static T[] Find(bool periodic, bool includeInactive)
        {
            if (!tracked) return UnityEngine.Object.FindObjectsOfType<T>(includeInactive);
            if (WalkDue(periodic)) Walk(periodic);
            int count = 0;
            for (int i = 0; i < known.Count; i++)
                if (Returned(known[i], includeInactive)) count++;
            var result = new T[count];
            int next = 0;
            for (int i = 0; i < known.Count && next < count; i++)
                if (Returned(known[i], includeInactive)) result[next++] = known[i];
            return result;
        }

        private static bool Returned(T candidate, bool includeInactive) =>
            candidate != null && (includeInactive || candidate.gameObject.activeInHierarchy);

        private static void Walk(bool periodic)
        {
            // Inactive objects are remembered too: one that is activated again later runs
            // no second native Start, and an active-only walk would have forgotten it.
            var found = UnityEngine.Object.FindObjectsOfType<T>(true);
            int missed = 0;
            if (seeded && periodic)
                foreach (var candidate in found)
                    if (candidate != null && !index.Contains(candidate) && candidate.gameObject.activeInHierarchy) missed++;
            known.Clear(); index.Clear();
            foreach (var candidate in found)
                if (candidate != null && index.Add(candidate)) known.Add(candidate);
            float now = Time.unscaledTime;
            seeded = true; lastWalk = now; nextWalk = now + fallback;
            if (missed > 0 && !reported)
            {
                // Once per type and world: evidence that a lifecycle hook is incomplete.
                reported = true;
                NativeSourceTracking.Log?.Invoke("MonMP discovery: the fallback search found " + missed + " active " +
                    typeof(T).Name + " object(s) that native initialization had not reported.");
            }
        }
    }
}
