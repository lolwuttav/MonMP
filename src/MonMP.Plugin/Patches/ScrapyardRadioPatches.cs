using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Scrapyard_Radio), "MouseRightDown")]
    internal static class ScrapyardRadioPickupPatch
    {
        private static bool Prefix(Scrapyard_Radio __instance)
        {
            var plugin = MonMP.Plugin.MonMPPlugin.Instance;
            bool running = plugin?.Session?.IsRunning == true;
            bool isHost = plugin?.Session?.IsHost == true;
            if (ScrapyardRadioPolicy.AllowNativePickup(running, isHost)) return true;
            return plugin?.World?.ScrapyardRadio.InterceptPickup(__instance) ?? false;
        }

        private static void Postfix(Scrapyard_Radio __instance)
        {
            var plugin = MonMP.Plugin.MonMPPlugin.Instance;
            plugin?.World?.ScrapyardRadio.NativePickupCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Scrapyard), "NewDay")]
    internal static class ScrapyardRadioNewDayPatch
    {
        private static bool Prefix(Scrapyard __instance, int day)
        {
            var plugin = MonMP.Plugin.MonMPPlugin.Instance;
            if (ScrapyardRadioPolicy.AllowNativeLifecycle(plugin?.Session?.IsRunning == true, plugin?.Session?.IsHost == true)) return true;
            plugin?.World?.ScrapyardRadio.SuppressGuestNewDay(__instance, day);
            return false;
        }

        private static void Postfix(Scrapyard __instance)
        {
            MonMP.Plugin.MonMPPlugin.Instance?.World?.ScrapyardRadio.NativeNewDayCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Scrapyard), "SpawnRadios")]
    internal static class ScrapyardRadioSpawnPatch
    {
        private static bool Prefix(Scrapyard __instance)
        {
            var plugin = MonMP.Plugin.MonMPPlugin.Instance;
            if (ScrapyardRadioPolicy.AllowNativeLifecycle(plugin?.Session?.IsRunning == true, plugin?.Session?.IsHost == true)) return true;
            plugin?.World?.ScrapyardRadio.SuppressGuestSpawn(__instance);
            return false;
        }

        private static void Postfix(Scrapyard __instance)
        {
            MonMP.Plugin.MonMPPlugin.Instance?.World?.ScrapyardRadio.NativeSpawnCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Scrapyard), "LoadAndUnlock")]
    internal static class ScrapyardRadioLoadPatch
    {
        private static bool Prefix(Scrapyard __instance)
        {
            var plugin = MonMP.Plugin.MonMPPlugin.Instance;
            if (ScrapyardRadioPolicy.AllowNativeLifecycle(plugin?.Session?.IsRunning == true, plugin?.Session?.IsHost == true)) return true;
            plugin?.World?.ScrapyardRadio.SuppressGuestLoad(__instance);
            return false;
        }

        private static void Postfix(Scrapyard __instance)
        {
            MonMP.Plugin.MonMPPlugin.Instance?.World?.ScrapyardRadio.NativeLoadCompleted(__instance);
        }
    }
}
