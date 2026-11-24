using System;

namespace MonMP.Plugin.Replication
{
    // A received day-skip may span many native refills. Replay its older minutes
    // for drug/timer/debuff effects without applying needs that the host reset.
    internal sealed class PersonalNeedsReplayScope : IDisposable
    {
        private static PersonalNeedsReplayScope? current;
        private readonly PersonalNeedsReplayScope? previous;
        private readonly float syrup, poutine, energy, anxiety;
        private readonly BasicNeeds_Manager needs;
        internal PersonalNeedsReplayScope(BasicNeeds_Manager needs)
        {
            previous = current; current = this; this.needs = needs;
            syrup = needs.GetMappleSyrup; poutine = needs.GetPoutine; energy = needs.GetEnergy; anxiety = needs.GetAnxiety;
            // These needs were reset on the host. Avoid replaying their old
            // anxiety shake/drop and hunger warnings while keeping native timers.
            Zero(needs);
        }
        internal static void ClampNeeds(BasicNeeds_Manager needs)
        {
            if (current == null) return;
            Zero(needs);
        }
        private static void Zero(BasicNeeds_Manager needs)
        { needs.GetMappleSyrup = needs.GetPoutine = needs.GetEnergy = needs.GetAnxiety = 0; }
        public void Dispose()
        {
            needs.GetMappleSyrup = syrup; needs.GetPoutine = poutine;
            needs.GetEnergy = energy; needs.GetAnxiety = anxiety;
            current = previous;
        }
    }
}
