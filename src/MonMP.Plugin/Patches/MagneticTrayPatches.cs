using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Mouse placement and both trigger/collision handlers funnel through this native method.
    // On guests it becomes a request; on the host it remains native when the entities are free.
    [HarmonyPatch(typeof(Tool_MagneticTray), "AddBolt", typeof(Hardware_Bolt))]
    internal static class MagneticTrayAddBoltPatch
    {
        private static void Postfix(Tool_MagneticTray __instance, Hardware_Bolt __0)
        {
            Replication.NativeItemDiscovery.Changed(__instance);
            if (__0 != null) Replication.NativeItemDiscovery.Changed(__0);
        }
        private static bool Prefix(Tool_MagneticTray __instance, Hardware_Bolt __0) =>
            MonMPPlugin.Instance?.Items?.MagneticTrayAdd(__instance, __0) != false;
    }

    [HarmonyPatch(typeof(Tool_MagneticTray), nameof(Tool_MagneticTray.MouseRightDown))]
    internal static class MagneticTrayRemoveAllPatch
    {
        private static bool Prefix(Tool_MagneticTray __instance)
        {
            foreach (var bolt in Replication.MagneticTrayNative.Slots(__instance))
                if (bolt != null) Replication.NativeItemDiscovery.Changed(bolt);
            Replication.NativeItemDiscovery.Changed(__instance);
            return MonMPPlugin.Instance?.Items?.MagneticTrayRemoveAll(__instance) != false;
        }
    }
}
