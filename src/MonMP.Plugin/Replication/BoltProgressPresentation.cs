using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // This value drives only the bolt transform and the labelled preview percentage.
    // Native BoltedPercent, terminal callbacks and item ownership always use host state.
    internal sealed class BoltProgressPresentation
    {
        internal const float MaximumLeadSeconds = 1.25f;
        internal float Shown { get; private set; }

        internal BoltProgressPresentation(float percent) => Shown = Clamp(percent);

        internal void Advance(float canonical, BoltToolKind kind, BoltToolDirection direction, float elapsed)
        {
            canonical = Clamp(canonical);
            float lead = kind == BoltToolKind.AirImpact ? MaximumLeadSeconds / 0.4f : MaximumLeadSeconds / 3f;
            float next = BoltToolPolicy.Advance(Shown, kind, direction, Math.Max(0, elapsed), true);
            // Never claim completion or removal before the actual host endpoint. A stalled
            // or denied tool command can show at most one bounded interval of speculation.
            Shown = direction == BoltToolDirection.Tighten
                ? Math.Max(canonical, Math.Min(next, Math.Min(canonical + lead, canonical >= 1 ? 1 : 0.99f)))
                : Math.Min(canonical, Math.Max(next, Math.Max(canonical - lead, canonical <= 0 ? 0 : 0.01f)));
        }

        internal bool Reconcile(float canonical, float elapsed)
        {
            canonical = Clamp(canonical);
            Shown += (canonical - Shown) * (1f - (float)Math.Exp(-Math.Max(0, elapsed) * 12f));
            if (Math.Abs(Shown - canonical) > 0.002f) return false;
            Shown = canonical;
            return true;
        }

        internal bool IsPending(float canonical) => Math.Abs(Shown - Clamp(canonical)) > 0.005f;
        private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));
    }
}
