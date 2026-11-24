using HarmonyLib;
using Leo;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(TamedSM), nameof(TamedSM.Pet))]
    internal static class SharedLeoFriendshipPatch
    {
        internal static void Postfix()
        {
            // Guest-request pets publish only after LeoGuestPet's rollback scope
            // has committed. A guest's mirrored Leo never owns friendship.
            if (!LeoGuestPet.Applying)
                MonMPPlugin.Instance?.World?.NpcInteractions?.HostFriendshipChanged();
        }
    }
}
