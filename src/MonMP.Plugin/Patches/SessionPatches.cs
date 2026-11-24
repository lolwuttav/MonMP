using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(EscapeMenuV2), nameof(EscapeMenuV2.Button_QuitGameConfirmation))]
    internal static class HostOrderlyExitPatch
    {
        private static bool Prefix(EscapeMenuV2 __instance) => MonMPPlugin.Instance?.AllowNativeExit(__instance) ?? true;
    }

    [HarmonyPatch(typeof(SaveSystem), "LoadPlayer", new[] { typeof(PlayerData) })]
    internal static class LocalProfileLoadPatch
    {
        private static void Postfix(PlayerData playerData)
        { if (playerData != null) MonMPPlugin.Instance?.RestoreLoadedPersonalState(); }
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.PauseGame))]
    internal static class LocalPausePatch
    {
        private static void Prefix(out float __state) => __state = Time.timeScale;
        private static void Postfix(float __state)
        {
            // Preserve the vanilla menu, input disable, and audio pause. Only simulation keeps running.
            if (MonMPPlugin.Instance?.Session?.IsRunning == true)
                Time.timeScale = __state > 0 ? __state : 1;
            MonMPPlugin.Instance?.World?.SaveGate.Enforce();
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Save), new[] { typeof(House_Location) })]
    internal static class GuestSavePatch
    {
        private static bool Prefix(House_Location save_Location, out Replication.OfflineSaveAncestry? __state)
        {
            __state = null;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.World?.PortableConsoleCharger.NativeConsoleSave(save_Location) == true) return false;
            if (plugin?.World?.SceneFurniture.NativeSave(save_Location) == true) return false;
            if (plugin?.GuestWorld?.SuppressDisk != true && plugin?.Session?.IsRunning != true)
            { __state = Replication.OfflineSaveAncestry.BeforeSave(); return true; }
            if (plugin?.GuestWorld?.SuppressDisk != true && plugin?.Session?.IsHost == true && plugin.World?.Saves != null)
            { plugin.World.Saves.Request(save_Location); return false; }
            // Native sleep always requests a bed save; the host's coordinated save covers guests.
            if (save_Location != House_Location.Bed || plugin?.GuestWorld?.SuppressDisk == true) plugin?.ReportSaveBlocked();
            return false;
        }
        private static void Postfix(Replication.OfflineSaveAncestry? __state) => __state?.AfterSave();
    }

    [HarmonyPatch(typeof(Furniture_SwitchButton), nameof(Furniture_SwitchButton.MouseDown))]
    internal static class SharedLightClickPatch
    {
        private static bool Prefix(Furniture_SwitchButton __instance)
        {
            if (MonMPPlugin.Instance?.World?.Sinks.Intercept(__instance) == true) return false;
            if (MonMPPlugin.Instance?.World?.BunkerWatering.Intercept(__instance) == true) return false;
            if (MonMPPlugin.Instance?.World?.Dollies.Intercept(__instance) == true) return false;
            var lights = MonMPPlugin.Instance?.Lights;
            if (lights?.Handles(__instance) == true) { lights.Click(__instance); return false; }
            return MonMPPlugin.Instance?.World?.Generator.InterceptKillSwitch(__instance) != true;
        }
    }

    [HarmonyPatch]
    internal static class SharedLightPowerPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Furniture_Light), "TurnOn");
            yield return AccessTools.Method(typeof(Furniture_Light), "TurnOff");
        }
        private static bool Prefix(Furniture_Light __instance) => MonMPPlugin.Instance?.Lights?.Handles(__instance) != true;
    }
}
