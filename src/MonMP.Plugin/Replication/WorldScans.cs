using System;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Admission for native registry walks (FindObjectsOfType). Each walk costs milliseconds on
    // a played save and cannot be divided, so the only way to keep it out of sight is to keep
    // walks apart:
    //  - at most one per rendered frame;
    //  - periodic recovery walks at least MinSpacing apart, so the per-second searches settle
    //    into separate phases instead of a run of consecutive slow frames;
    //  - a periodic walk prefers a frame without a physics step, which already costs about
    //    twice a plain frame, for at most PhysicsDeferral.
    // A refused search stays due and is retried on the next Update. Initial and forced searches
    // (periodic: false) only obey the per-frame rule and keep their old timing.
    //
    // 0.9.58 aligned every deadline to whole-interval boundaries and let the due searches share
    // one MonoBehaviour/Interactable query. That lowered total CPU but put the shared query,
    // every filter pass and the staged follow-ups into two or three consecutive frames once a
    // second: a visible rhythmic host stutter. Deadlines are relative again and never shared.
    internal static class WorldScans
    {
        internal const float MinSpacing = 0.06f, PhysicsDeferral = 0.05f;
        private static int claimed = -1, physicsFrame = -1;
        private static float lastPeriodic = float.NegativeInfinity, physicsRefusedAt = -1f;
        internal static void Reset()
        {
            claimed = -1; physicsFrame = -1;
            lastPeriodic = float.NegativeInfinity; physicsRefusedAt = -1f;
        }
        // Called from the plugin's FixedUpdate, which runs before Update in the same frame.
        internal static void NotePhysicsStep() => physicsFrame = Time.frameCount;
        // The established interval measured from the search that just ran. Relative deadlines
        // keep the phases that the spacing rule separated.
        internal static float NextDeadline(float now, float interval) => now + interval;
        // Kept for the world Update pass; searches no longer share a query.
        internal static Batch BeginBatch(int dueQueries, int interactableQueries = 0) => new Batch();
        internal readonly struct Batch : IDisposable
        {
            public void Dispose() { }
        }
        // A tracked type's recurring search reads its remembered components (NativeSources)
        // and performs no registry walk, so it neither needs nor consumes an admission.
        internal static bool TryClaim<T>(bool periodic = true) where T : MonoBehaviour =>
            !NativeSources<T>.WalkDue(periodic) || TryClaim(periodic);
        internal static bool TryClaim(bool periodic = true)
        {
            int frame = Time.frameCount;
            if (claimed == frame) return false;
            if (periodic)
            {
                float now = Time.unscaledTime;
                // A clock that restarted (new scene/session statics) must not block forever.
                if (now < lastPeriodic) lastPeriodic = float.NegativeInfinity;
                if (now - lastPeriodic < MinSpacing) return false;
                if (physicsFrame == frame)
                {
                    if (physicsRefusedAt < 0f || now < physicsRefusedAt) physicsRefusedAt = now;
                    if (now - physicsRefusedAt < PhysicsDeferral) return false;
                }
                physicsRefusedAt = -1f;
                lastPeriodic = now;
            }
            claimed = frame;
            return true;
        }
        internal static T[] Find<T>(bool periodic, bool includeInactive = false) where T : MonoBehaviour =>
            NativeSources<T>.Find(periodic, includeInactive);
    }
}
