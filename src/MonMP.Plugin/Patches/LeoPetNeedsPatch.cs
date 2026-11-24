using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // TamedSM.Pet also calms the local player. A validated guest pet runs that
    // method on the host for Leo's shared state, without calming the host player.
    [HarmonyPatch(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Pet_Leo))]
    internal static class SharedLeoRemotePetNeedsPatch
    {
        internal static bool Prefix() => !LeoGuestPet.Applying;
    }
}
