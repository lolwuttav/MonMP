using System;

namespace MonMP.Plugin.Replication
{
    // A native door opens by impulse; its joint limit is not its promised resting angle.
    // Start with a small visible nudge, then let native motion catch that nudge naturally.
    // Only an obstructed/no-motion result needs the bounded, continuous fallback correction.
    internal sealed class VehicleDoorOpeningPresentation
    {
        internal const float NudgeDegrees = 8f;
        private const double CaptureGraceSeconds = 0.2, CorrectionSeconds = 0.3;
        private bool active, observedOpen;
        private float direction, floor;
        private double confirmedAt = double.PositiveInfinity;
        internal bool Active => active;
        internal void Begin(float angle, bool driver)
        {
            direction = driver ? 1f : -1f;
            floor = Math.Max(NudgeDegrees, Math.Max(0, angle * direction));
            active = true; observedOpen = false; confirmedAt = double.PositiveInfinity;
        }
        internal void Confirm(double now)
        { if (active) confirmedAt = now; }
        internal void Observe(bool open, float angle)
        {
            if (!active) return;
            if (!open) { if (observedOpen) Clear(); return; }
            observedOpen = true;
            if (angle * direction >= floor) Clear();
        }
        internal float Target(float authority, double now)
        {
            if (!active) return authority;
            double elapsed = now - confirmedAt - CaptureGraceSeconds;
            if (elapsed >= CorrectionSeconds) { Clear(); return authority; }
            float weight = elapsed <= 0 ? 1f : 1f - (float)(elapsed / CorrectionSeconds);
            // Fade the offset, rather than switching between unrelated 45-degree and host
            // targets at a timer/30-degree threshold. The fallback has no endpoint jump.
            float progress = authority * direction;
            return direction * (progress + Math.Max(0, floor - progress) * weight);
        }
        internal void Clear() { active = false; observedOpen = false; confirmedAt = double.PositiveInfinity; }
    }
}
