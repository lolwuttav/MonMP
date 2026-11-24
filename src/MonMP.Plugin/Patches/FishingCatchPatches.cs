using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Item_Fish), nameof(Item_Fish.DropFromFishingRod))]
    internal static class SharedFishingCatchReleasePatch
    {
        private static bool Prefix(Item_Fish __instance)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true) return true;
            FishingCatchRelease.Drop(__instance);
            return false;
        }

        private static void Postfix(Item_Fish __instance)
        {
            // Release the grip before an ownership echo can see an unparented, locally owned
            // catch and put it back in the hand. Rod unequip runs this same native release.
            MonMPPlugin.Instance?.Items?.ToolBoltReleased(__instance, false);
        }
    }
}
