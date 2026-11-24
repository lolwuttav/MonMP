using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.HardMode_Permadeath))]
    internal static class GuestPermadeathSavePatch
    {
        internal static bool Guarded => MonMPPlugin.Instance?.GuestWorld?.SuppressDisk == true ||
            (MonMPPlugin.Instance?.Session?.IsRunning == true && !MonMPPlugin.Instance.Session.IsHost);
        private static bool Prefix()
        {
            if (!Guarded) return true;
            MonMPPlugin.Instance!.ReportSaveBlocked(); return false;
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.DeleteSaveName))]
    internal static class GuestSaveDeletionPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!GuestPermadeathSavePatch.Guarded) return true;
            MonMPPlugin.Instance!.ReportSaveBlocked(); __result = false; return false;
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetPlayerFromSave), new System.Type[0])]
    internal static class GuestDiskReadPatch
    {
        private static bool Prefix(ref PlayerData? __result)
        {
            var loading = MonMPPlugin.Instance?.GuestWorld;
            if (loading?.SuppressDisk != true) return true;
            loading.NoteBlockedRead(); __result = null; return false;
        }
    }

    [HarmonyPatch(typeof(GlobalClassManager), "get_HasASaveForThisGame")]
    internal static class GuestHasSavePatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (MonMPPlugin.Instance?.GuestWorld?.SuppressDisk != true) return true;
            __result = false; return false;
        }
    }

    [HarmonyPatch(typeof(GlobalClassManager), "CheckIfItemNeedToBeDeleted")]
    internal static class GuestSceneDeletionPatch
    {
        private static bool Prefix()
        {
            var loading = MonMPPlugin.Instance?.GuestWorld;
            if (loading?.SuppressDisk != true) return true;
            loading.NotePreservedObject(); return false;
        }
    }

    [HarmonyPatch(typeof(GlobalClassManager), "Awake")]
    internal static class GuestConfigurationPatch
    {
        private static void Postfix() => MonMPPlugin.Instance?.GuestWorld?.ApplyConfiguration();
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.HardMode_LoadSave))]
    internal static class GuestHardModeLayoutPatch
    {
        private static void Prefix(Gameplay __instance) => MonMPPlugin.Instance?.GuestWorld?.PrepareNativeHardMode(__instance);
    }

    [HarmonyPatch(typeof(UI_BlackScreenWaitForSaveSystem), nameof(UI_BlackScreenWaitForSaveSystem.FadeBlackScreen))]
    internal static class GuestLoadingScreenPatch
    {
        private static bool Prefix()
        {
            var loading = MonMPPlugin.Instance?.GuestWorld;
            if (loading?.HoldPresentation != true) return true;
            loading.NativeFinished(); return false;
        }
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.ResumeGame))]
    internal static class GuestLoadingInputPatch
    {
        private static void Postfix()
        { MonMPPlugin.Instance?.GuestWorld?.HoldInputs(); MonMPPlugin.Instance?.World?.SaveGate.Enforce(); MonMPPlugin.Instance?.Chat?.HoldInputs(); }
    }
}
