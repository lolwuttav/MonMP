using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed class ComputerTimeScope : IDisposable
    {
        private readonly uint previous;
        internal static uint User { get; private set; } = ItemLease.Unowned;
        internal static bool Active => User != ItemLease.Unowned;
        private static float syrup, poutine, energy, anxiety;
        internal ComputerTimeScope(uint user) { previous = User; User = user; }
        internal static void BeforeNeeds(BasicNeeds_Manager needs)
        {
            syrup = needs.GetMappleSyrup; poutine = needs.GetPoutine; energy = needs.GetEnergy; anxiety = needs.GetAnxiety;
        }
        // Injected before native UpdateUI and therefore before its death/warning checks.
        internal static void ClampNeeds(BasicNeeds_Manager needs)
        {
            if (Active)
            {
                needs.GetMappleSyrup = ComputerNeedsPolicy.Limit(syrup, needs.GetMappleSyrup);
                needs.GetPoutine = ComputerNeedsPolicy.Limit(poutine, needs.GetPoutine);
                needs.GetEnergy = ComputerNeedsPolicy.Limit(energy, needs.GetEnergy);
                // Preserve personal metabolism and save ownership, while giving
                // all players the same native anxiety benefit from skipped time.
                needs.GetAnxiety = ComputerNeedsPolicy.AnxietyAfterMinute(anxiety);
            }
            PersonalNeedsReplayScope.ClampNeeds(needs);
        }
        public void Dispose() => User = previous;
    }
}
