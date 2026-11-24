using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // The quad's native holder mutates and parents the chainsaw directly. Guest
    // copies must let the host commit that action so the holder pose is shared.
    [HarmonyPatch(typeof(Vehicle_Chainsaw_Holder), "OnTriggerEnter")]
    internal static class SharedChainsawHolderPatch
    {
        private static bool Prefix(Vehicle_Chainsaw_Holder __instance, UnityEngine.Collider other)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return plugin.Items?.ChainsawHolderTrigger(__instance, other) ?? plugin.Session.IsHost;
        }
    }
}
