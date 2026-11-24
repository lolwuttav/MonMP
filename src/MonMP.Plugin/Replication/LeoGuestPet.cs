using System;
using Leo;

namespace MonMP.Plugin.Replication
{
    internal static class LeoGuestPet
    {
        [ThreadStatic] private static int applying;
        internal static bool Applying => applying != 0;

        internal static void Apply(TamedSM native)
        {
            int actor = (int)Actor_Type.Leo;
            int friendship = GameManager.Get_Friendship[actor];
            bool talked = GameManager.Get_Friendship_TalkedToday[actor];
            applying++;
            try { native.Pet(); }
            catch
            {
                // Native UI/animation dependencies can fail after incrementing friendship.
                // A failed request must not earn another point when the guest retries it.
                GameManager.Get_Friendship[actor] = friendship;
                GameManager.Get_Friendship_TalkedToday[actor] = talked;
                throw;
            }
            finally { applying--; }
        }
    }
}
