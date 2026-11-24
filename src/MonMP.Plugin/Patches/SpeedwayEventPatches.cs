using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.Effects;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // The speedway's events, one player at a time per venue, watched by everyone else
    // (SharedSpeedwayEvents).
    internal static class SpeedwayEventHooks
    {
        internal static SharedSpeedwayEvents? Events => MonMPPlugin.Instance?.World?.SpeedwayEvents;
    }

    // Time trials: a start line claims the trial lease. Nothing fires for a watcher on the lines and
    // checkpoints shown for someone else's run.
    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerEntered_Drag))]
    internal static class SpeedwayDragEnteredPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true && SpeedwayEventHooks.Events?.AllowTrialStart(SharedSpeedwayEvents.TrialDrag) != false;
    }

    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerEntered_Oval))]
    internal static class SpeedwayOvalEnteredPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true && SpeedwayEventHooks.Events?.AllowTrialStart(SharedSpeedwayEvents.TrialOval) != false;
    }

    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerEntered_Track))]
    internal static class SpeedwayTrackEnteredPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true && SpeedwayEventHooks.Events?.AllowTrialStart(SharedSpeedwayEvents.TrialTrack) != false;
    }

    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerCross_DragFinishLine))]
    internal static class SpeedwayDragFinishPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true;
    }

    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerCross_OvalCheckpoint))]
    internal static class SpeedwayOvalCheckpointPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true;
    }

    [HarmonyPatch(typeof(Speedway), nameof(Speedway.PlayerCross_TrackCheckpoint))]
    internal static class SpeedwayTrackCheckpointPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksTrialCrossing != true;
    }

    // Championship: one registered player; a watcher's checkpoints and start lines are inert.
    [HarmonyPatch(typeof(Event_Championship), nameof(Event_Championship.InscriptionFormClicked))]
    internal static class ChampionshipInscriptionPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.AllowRegistration(SpeedwayVenue.Championship) != false;
    }

    [HarmonyPatch(typeof(Event_Championship), nameof(Event_Championship.StartDetector_PlayerIn))]
    internal static class ChampionshipStartLinePatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.AllowChampionshipStart() != false;
    }

    [HarmonyPatch(typeof(Event_Championship), "Player_Checkpoint")]
    internal static class ChampionshipCheckpointPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksChampionship != true;
    }

    // A guest's championship win: the host spawns the trophy and pays.
    [HarmonyPatch(typeof(Event_Championship), "Championship_NextEvent", typeof(bool))]
    internal static class ChampionshipNextEventPatch
    {
        private static void Prefix() => SpeedwayEventHooks.Events?.BeforeWin(SpeedwayVenue.Championship);
        private static void Finalizer() => SpeedwayEventHooks.Events?.AfterWin(SpeedwayVenue.Championship);
    }

    // Derby: one registered player; the derby stays open at noon for someone else's registration.
    [HarmonyPatch(typeof(Event_DerbyDemolition), nameof(Event_DerbyDemolition.InscriptionFormClicked))]
    internal static class DerbyInscriptionPatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.AllowRegistration(SpeedwayVenue.Derby) != false;
    }

    [HarmonyPatch(typeof(Event_DerbyDemolition), nameof(Event_DerbyDemolition.StartLine_PlayerEntered))]
    internal static class DerbyStartLinePatch
    {
        private static bool Prefix() => SpeedwayEventHooks.Events?.BlocksDerbyStart != true;
    }

    [HarmonyPatch(typeof(Event_DerbyDemolition), "Tick", typeof(int))]
    internal static class DerbyTickPatch
    {
        private static bool Prefix(int tick) => tick != 720 || SpeedwayEventHooks.Events?.KeepsDerbyOpen != true;
    }

    // A registration that never started: the native close forgets to lower the raised cars (#284).
    [HarmonyPatch(typeof(Event_DerbyDemolition), nameof(Event_DerbyDemolition.CloseStore))]
    internal static class DerbyCloseStorePatch
    {
        private static void Prefix(Event_DerbyDemolition __instance, out bool __state) => __state = SharedSpeedwayEvents.DerbyCloseWouldStrandCars(__instance);
        private static void Postfix(Event_DerbyDemolition __instance, bool __state) => SpeedwayEventHooks.Events?.DerbyClosed(__instance, __state);
    }

    [HarmonyPatch(typeof(Event_DerbyDemolition), "RoundCompleted", typeof(bool), typeof(bool))]
    internal static class DerbyRoundCompletedPatch
    {
        private static void Prefix() => SpeedwayEventHooks.Events?.BeforeWin(SpeedwayVenue.Derby);
        private static void Finalizer() => SpeedwayEventHooks.Events?.AfterWin(SpeedwayVenue.Derby);
    }

    // Race sounds of this player's own event, for those watching it.
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_RaceCountdown))]
    internal static class RaceCountdownRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Countdown); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_RaceStart))]
    internal static class RaceStartRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Start); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_Race_Checkpoint))]
    internal static class RaceCheckpointRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Checkpoint); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_Race_Finish))]
    internal static class RaceFinishRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Finish); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_Race_Whistle))]
    internal static class RaceWhistleRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Whistle); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_Race_CarDead))]
    internal static class RaceCarDeadRelayPatch
    { private static void Postfix() => SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.CarDead); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BuggyBuildCompleted))]
    internal static class RaceChampionRelayPatch
    { private static void Postfix() { if (!RogerUnlockPresentation.NativeGreetingActive) SpeedwayEventHooks.Events?.LocalCue(SharedSpeedwayEvents.Cue.Champion); } }

    // The event messages this player's own event shows, for those watching it.
    [HarmonyPatch(typeof(UI_EventMessage), nameof(UI_EventMessage.Show), typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType))]
    internal static class SpeedwayMessageShowPatch
    {
        private static void Postfix(string _text, string _name, float EndTime, UI_EventMessage.EventIconType IconType)
        {
            if (!RogerUnlockPresentation.NativeGreetingActive && !SharedWorldNotices.Matches(_text, _name))
                SpeedwayEventHooks.Events?.LocalMessage(_text, _name, EndTime, IconType, false);
        }
    }

    [HarmonyPatch(typeof(UI_EventMessage), nameof(UI_EventMessage.Hide))]
    internal static class SpeedwayMessageHidePatch
    {
        private static void Postfix() => SpeedwayEventHooks.Events?.LocalMessage(null, null, 0f, UI_EventMessage.EventIconType.Info, true);
    }

    // Derby dents and repairs of the holder's cars, for everyone's copies.
    [HarmonyPatch(typeof(DamageHandler), nameof(DamageHandler.Enqueue), typeof(Collision), typeof(float))]
    internal static class DerbyDentRelayPatch
    {
        private static void Postfix(DamageHandler __instance, Collision collision, float accelerationMagnitude, bool __result)
        {
            if (__result && __instance.meshDeform) SpeedwayEventHooks.Events?.LocalDent(__instance, collision, accelerationMagnitude);
        }
    }

    [HarmonyPatch(typeof(DamageHandler), nameof(DamageHandler.Repair))]
    internal static class DerbyRepairRelayPatch
    {
        private static void Postfix(DamageHandler __instance) => SpeedwayEventHooks.Events?.LocalRepair(__instance);
    }
}
