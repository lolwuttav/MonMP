using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(GameManager), "set_SETTING_Cheats")]
    internal static class CheatSettingAuthorityPatch
    {
        private static bool Prefix(bool value)
        {
            var session = MonMPPlugin.Instance?.Session;
            if (SharedCheats.Applying || session?.IsRunning != true) return true;
            return session.IsHost && (value || !session.Cheats.Enabled);
        }
        private static void Postfix()
        {
            var session = MonMPPlugin.Instance?.Session;
            if (!SharedCheats.Applying && session?.IsHost == true) session.SetCheatsEnabled(GameManager.SETTING_Cheats);
        }
    }
    [HarmonyPatch(typeof(EscapeMenu_GeneralV2), "Cheats_Activate")]
    internal static class CheatActivationAuthorityPatch
    {
        private static bool Prefix()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            plugin.ReportItemUseUnavailable("Only the host can enable cheats for this session."); return false;
        }
    }
    [HarmonyPatch(typeof(EscapeMenu_GeneralV2), "Update_CheatFriendshipWindow")]
    internal static class CheatMenuPermissionPatch
    {
        private static void Postfix(EscapeMenu_GeneralV2 __instance) => MonMPPlugin.Instance?.Cheats?.RefreshMenu(__instance);
    }
    [HarmonyPatch]
    internal static class NativeCheatActionPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { "TPVehicle", "TPPlayer", "SpawnThisObject", "ChangeWeather", "ChangeGravity", "AddOneHour", "AddOneDay", "ResetNeeds", "GodSpeed", "Save", "Plants_Water", "Plants_Desinfect", "Add1Thousand", "RefillAllCars" })
            {
                Type[] arguments = name == "GodSpeed" ? new[] { typeof(bool) } :
                    name == "TPVehicle" || name == "TPPlayer" || name == "SpawnThisObject" || name == "ChangeWeather" || name == "ChangeGravity"
                        ? new[] { typeof(int) } : Type.EmptyTypes;
                var method = AccessTools.Method(typeof(SpecialMenu), name, arguments);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported native cheat action signature: " + name);
                yield return method;
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(SpecialMenu __instance, MethodBase __originalMethod, object[] __args) =>
            MonMPPlugin.Instance?.Cheats?.NativeAction(__instance, __originalMethod.Name, __args) ?? true;
        private static void Postfix(SpecialMenu __instance, MethodBase __originalMethod)
        {
            if (__originalMethod.Name == "ChangeGravity" && MonMPPlugin.Instance?.Cheats?.CanUse == true)
                (AccessTools.Field(typeof(SpecialMenu), "Dropdown_Gravity").GetValue(__instance) as UnityEngine.UI.Dropdown)?.SetValueWithoutNotify(0);
            if (__originalMethod.Name == "ChangeGravity" && MonMPPlugin.Instance?.Session?.IsHost == true)
                MonMPPlugin.Instance.Session.SetCheatGravity(Physics.gravity.y);
        }
    }
    // A guest's authorized shared action must not resume or take over the host's pause UI.
    [HarmonyPatch(typeof(EscapeMenuV2), nameof(EscapeMenuV2.ResumeGame), new[] { typeof(bool) })]
    internal static class RemoteCheatResumePatch
    {
        private static bool Prefix() => !SharedCheats.ExecutingRemote && (MonMPPlugin.Instance?.AllowAppearanceResume() ?? true);
    }
}
