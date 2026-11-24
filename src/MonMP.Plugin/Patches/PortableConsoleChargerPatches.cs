using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Furniture_PortableGameConsole_Charger), nameof(Furniture_PortableGameConsole_Charger.MouseDown))]
    internal static class PortableConsoleChargerInteractionPatch
    {
        private static bool Prefix(Furniture_PortableGameConsole_Charger __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            var shared = SharedPortableConsoleCharger.Active;
            if (shared == null) return plugin.Session.IsHost;
            return shared.Intercept(__instance);
        }

        private static void Postfix(Furniture_PortableGameConsole_Charger __instance)
        {
            SharedPortableConsoleCharger.Active?.NativeMouseDownCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Furniture_PortableGameConsole_Charger), "Tick", new[] { typeof(int) })]
    internal static class PortableConsoleChargerTickAuthorityPatch
    {
        private static bool Prefix(Furniture_PortableGameConsole_Charger __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            return SharedPortableConsoleCharger.Active?.AllowNativeChargeTick(__instance) ?? false;
        }
    }

    [HarmonyPatch(typeof(PortableGameConsole), "set_Get_BatteryLife")]
    internal static class PortableConsoleBatteryAuthorityPatch
    {
        private static bool Prefix()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            return SharedPortableConsoleCharger.Active?.AllowNativeBatteryWrite() ?? false;
        }
    }

    [HarmonyPatch(typeof(PlayerVisualHands), "set_Get_ConsoleLocation")]
    internal static class PortableConsoleLocationAuthorityPatch
    {
        private static bool Prefix()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            return SharedPortableConsoleCharger.Active?.AllowNativeLocationWrite() ?? false;
        }
    }

    internal static class PortableConsoleChargerPatchTargets
    {
        internal static IEnumerable<MethodBase> RequiredTargets()
        {
            yield return AccessTools.Method(typeof(Game_Default_Menu), "Saving")!;
            yield return AccessTools.Method(typeof(Furniture_PortableGameConsole_Charger), nameof(Furniture_PortableGameConsole_Charger.MouseDown))!;
            yield return AccessTools.Method(typeof(Furniture_PortableGameConsole_Charger), "Tick", new[] { typeof(int) })!;
            yield return AccessTools.PropertySetter(typeof(PortableGameConsole), nameof(PortableGameConsole.Get_BatteryLife))!;
            yield return AccessTools.PropertySetter(typeof(PlayerVisualHands), nameof(PlayerVisualHands.Get_ConsoleLocation))!;
        }
    }

    [HarmonyPatch(typeof(Game_Default_Menu), "Saving")]
    internal static class PortableConsoleSavePatch
    {
        private static void Postfix(Game_Default_Menu __instance, ref System.Collections.IEnumerator __result)
        {
            if (SharedPortableConsoleCharger.Active is { } shared && MonMPPlugin.Instance?.Session?.IsRunning == true)
                __result = shared.CoordinateConsoleSave(__instance, __result);
        }
    }
}
