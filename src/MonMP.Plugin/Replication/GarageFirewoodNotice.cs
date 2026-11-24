namespace MonMP.Plugin.Replication
{
    // Shared milestones are announcements on live transitions, not history to
    // replay when a guest joins or repairs an already completed world.
    internal sealed class GarageFirewoodNotice
    {
        private bool initialized, announced;
        private int previous;

        internal bool Observe(int amount, int required, bool snapshot)
        {
            bool complete = required > 0 && amount >= required;
            bool notify = !snapshot && initialized && !announced && previous < required && complete;
            initialized = true; previous = amount;
            if (complete) announced = true;
            return notify;
        }
    }
}
