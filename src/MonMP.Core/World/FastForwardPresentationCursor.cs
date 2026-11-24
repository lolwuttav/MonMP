using System;

namespace MonMP.Core.World
{
    // The retained skip identity survives WorldChannel coalescing. A completed
    // identity is retired once its personal minutes drain, while initial history
    // only establishes a baseline. Unchanged clock heartbeats cannot hold input.
    public sealed class FastForwardPresentationCursor
    {
        public const float TimeoutSeconds = 10;
        private int start, ticks, retiredStart;
        private ushort target, retiredTarget;
        private bool running, initialized, retired;
        private float observedAt;
        public void Observe(ClockState state, float now)
        {
            bool baseline = !initialized;
            initialized = true;
            if (state.FastForwardTarget == 0)
            {
                if (running) observedAt = now;
                running = false; ticks = state.TotalTicks;
                return;
            }
            bool sameRetired = retired && retiredStart == state.FastForwardStart && retiredTarget == state.FastForwardTarget;
            if (!state.FastForwardRunning && (baseline || sameRetired))
            {
                retired = true; retiredStart = state.FastForwardStart; retiredTarget = state.FastForwardTarget;
                target = 0; running = false; ticks = state.TotalTicks;
                return;
            }
            float previous = Progress;
            bool newSkip = target == 0 || start != state.FastForwardStart || target != state.FastForwardTarget;
            bool started = state.FastForwardRunning && !running;
            bool ended = !state.FastForwardRunning && running;
            start = state.FastForwardStart; target = state.FastForwardTarget; ticks = state.TotalTicks;
            if (newSkip || started || ended || state.FastForwardRunning && Progress > previous) observedAt = now;
            running = state.FastForwardRunning;
            // Native entry may be cancelled before its first minute. An explicit
            // new running transition can reuse that same start/target safely.
            if (running) retired = false;
        }
        public void Progressed(float now) { if (target > 0) observedAt = now; }
        public void RetireIfDrained(bool personalPending)
        {
            if (running || personalPending) return;
            if (target > 0) { retired = true; retiredStart = start; retiredTarget = target; }
            target = 0;
        }
        public bool Active(float now, bool personalPending) => target > 0 &&
            (running || personalPending) && now - observedAt >= 0 && now - observedAt < TimeoutSeconds;
        public float Progress => target == 0 ? 0 : Math.Max(0f, Math.Min(1f, (float)(ticks - start) / target));
        public void Reset()
        {
            start = ticks = retiredStart = 0; target = retiredTarget = 0;
            running = initialized = retired = false; observedAt = 0;
        }
    }
}
