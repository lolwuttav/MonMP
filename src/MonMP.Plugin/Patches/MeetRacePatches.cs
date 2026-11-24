using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // One night meet race at a time across the session (see SharedMeetRace).
    internal static class MeetRacePatches
    {
        [HarmonyPatch(typeof(UI_GenericWindow_MeetsManager), "Bet_Done")]
        internal static class BetPatch
        {
            private static bool Prefix(UI_GenericWindow_MeetsManager __instance, int betIndex) =>
                MonMPPlugin.Instance?.World?.MeetRace?.AllowBet(__instance, betIndex) ?? true;
        }

        [HarmonyPatch(typeof(RaceManager), nameof(RaceManager.PlayerCompletedTheRace))]
        internal static class WinPatch
        {
            private static void Prefix() => MonMPPlugin.Instance?.World?.MeetRace?.PlayerWon();
            private static void Postfix() => MonMPPlugin.Instance?.World?.MeetRace?.NativeResultFinished();
        }

        [HarmonyPatch(typeof(RaceManager), nameof(RaceManager.AICompletedTheRace))]
        internal static class LossPatch
        {
            private static void Prefix() => MonMPPlugin.Instance?.World?.MeetRace?.PlayerLost();
            private static void Postfix() => MonMPPlugin.Instance?.World?.MeetRace?.NativeResultFinished();
        }

        [HarmonyPatch(typeof(RaceManager), "StopRace")]
        internal static class TimeoutPatch
        {
            private static void Prefix() => MonMPPlugin.Instance?.World?.MeetRace?.PlayerLost();
            private static void Postfix() => MonMPPlugin.Instance?.World?.MeetRace?.NativeResultFinished();
        }

        [HarmonyPatch(typeof(UI_GenericWindow_MeetsManager), "ClaimReward")]
        internal static class RewardPatch
        {
            private static bool Prefix(UI_GenericWindow_MeetsManager __instance, int rewardID) =>
                MonMPPlugin.Instance?.World?.MeetRace?.AllowReward(__instance, rewardID) ?? true;
        }

        // The host's meet stays open while another player is mid-race; the store tick retries.
        [HarmonyPatch(typeof(Store_MeetManager), nameof(Store_MeetManager.CloseStore))]
        internal static class ClosePatch
        {
            private static bool Prefix() => MonMPPlugin.Instance?.World?.MeetRace?.BlocksClose != true;
        }
    }
}
