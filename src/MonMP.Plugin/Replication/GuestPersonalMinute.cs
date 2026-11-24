namespace MonMP.Plugin.Replication
{
    internal static class GuestPersonalMinute
    {
        internal static void Tick(BasicNeeds_Manager needs, Gameplay? gameplay, bool sleeping, int minute, uint computerUser,
            bool dead = false, bool historical = false, uint localPeer = MonMP.Core.World.ItemLease.Unowned, bool saving = false)
        {
            bool originalSaving = needs.Get_isSaving;
            try
            {
                needs.Get_isSaving = saving;
                using (historical ? new PersonalNeedsReplayScope(needs) : null)
                using (new ComputerTimeScope(computerUser))
                {
                    if (!dead) needs.Tick(sleeping, computerUser != MonMP.Core.World.ItemLease.Unowned && computerUser == localPeer);
                    // Guests suppress UniStorm's minute delegates along with world simulation.
                    // Keep personal vehicle calming and interaction lighting. Historical
                    // replay remains inside the scope that discards reset need changes.
                    gameplay?.Tick(minute);
                }
            }
            finally { needs.Get_isSaving = originalSaving; }
        }
    }
}
