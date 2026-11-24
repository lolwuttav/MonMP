using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Gameplay), "PermaDeath")]
    internal static class MultiplayerPermadeathCoroutinePatch
    {
        private static void Postfix(ref System.Collections.IEnumerator __result)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning == true && plugin.World != null)
                __result = Replication.MultiplayerPermadeath.Coordinate(__result, plugin.World, plugin.Session.IsHost);
        }
    }

}
