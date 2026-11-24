using System;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // A guest's phone call answers natively (the quotes and messages are its own), but what it buys goes
    // to the host: the native "press 1" handlers check the guest's private wallet and change only its own
    // world. The answered service is remembered here and the matching key press becomes a host request.
    internal static class GuestPhoneCall
    {
        internal static Furniture_Phone? Phone;
        internal static int Result;
        internal static float At;
        internal static bool Ringing;
        internal static Guid MotherRequestId;
        private static readonly ToDoList_Line[] PavedByKey =
            { ToDoList_Line.Advanced_PavedToHome, ToDoList_Line.Advanced_PavedFrancis, ToDoList_Line.Advanced_PavedHomeParking, ToDoList_Line.Advanced_PavedRoadToSugarshack };

        // Native RingingCompleted results: 2 speedway fuel (10 s to press 1), 3 Construction Turbo (25 s,
        // keys 1-4), 4 hydro cut bill (10 s), 5 police tickets (18 s). One order per call, as natively.
        internal static bool Intercept(Furniture_Phone phone, string button)
        {
            var progress = MonMPPlugin.Instance?.World?.Progress;
            if (progress?.BlocksLocalChanges != true || phone != Phone || !phone.CanUsePhoneButton) return false;
            float age = Time.time - At;
            switch (Result)
            {
                case 2 when button == "1" && age < 10f:
                    Result = 0; return progress.RequestPhoneService(SharedProgress.PhoneService.SpeedwayFuel);
                case 4 when button == "1" && age < 10f:
                    Result = 0; return progress.RequestPhoneService(SharedProgress.PhoneService.HydroBill);
                case 5 when button == "1" && age < 18f:
                    Result = 0; return progress.RequestPhoneService(SharedProgress.PhoneService.PoliceTickets);
                case 3 when age < 25f && button.Length == 1 && button[0] >= '1' && button[0] <= '4':
                    Result = 0;
                    try { Singleton<UI_EventMessage>.i?.Hide(); } catch (Exception) { }
                    return progress.RequestPaved(PavedByKey[button[0] - '1']);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_Phone), "RingingCompleted")]
    internal static class GuestPhoneAnsweredPatch
    {
        private static bool Prefix(Furniture_Phone __instance, string ___NumberComposed, ref int __result)
        {
            GuestPhoneCall.Ringing = true;
            GuestPhoneCall.MotherRequestId = Guid.Empty;
            var plugin = MonMPPlugin.Instance;
            if (___NumberComposed == "5552109" && plugin?.Session?.IsRunning == true && !plugin.Session.IsHost)
            {
                if (plugin.World?.NpcInteractions is { } npcs)
                    GuestPhoneCall.MotherRequestId = npcs.RequestMotherPhoneCall(__instance);
                else
                    UnityEngine.Object.FindObjectOfType<UI_ErrorMessage>()?.Show("This call cannot be completed right now.");
                // End the native ring as an answered call while the host result is pending.
                // If the host says Mother was already called today, the reply plays the native busy response.
                __result = 1;
                return false;
            }
            var information = plugin?.World?.Information;
            if (___NumberComposed == "5554141" && information != null && information.TryShowPolicePhone(out int result))
            { __result = result; return false; }
            return true;
        }
        private static void Postfix(Furniture_Phone __instance, int __result)
        {
            GuestPhoneCall.Phone = __instance; GuestPhoneCall.Result = __result; GuestPhoneCall.At = Time.time;
        }
        private static void Finalizer() => GuestPhoneCall.Ringing = false;
    }

    [HarmonyPatch(typeof(Furniture_Phone), nameof(Furniture_Phone.ButtonPressed))]
    internal static class GuestPhoneKeyPatch
    {
        private static bool Prefix(Furniture_Phone __instance, string button)
        {
            if (!GuestPhoneCall.Intercept(__instance, button)) return true;
            Singleton<SOUND_Manager>.i?.PlaySound_Phone_Dial();
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_Phone), nameof(Furniture_Phone.PutApareilleBack))]
    internal static class GuestPhoneHangupPatch
    {
        private static void Postfix(Furniture_Phone __instance)
        {
            if (GuestPhoneCall.Phone != __instance) return;
            GuestPhoneCall.MotherRequestId = Guid.Empty;
            GuestPhoneCall.Phone = null; GuestPhoneCall.Result = 0;
        }
    }

    // Hydro's start-construction letter (555-4743 before the build) ships into the caller's mailbox.
    [HarmonyPatch(typeof(Store_PostOffice), nameof(Store_PostOffice.ShipThisLetterToPlayer))]
    internal static class GuestPhoneLetterPatch
    {
        private static bool Prefix(Envelope_Type envelope_Type) =>
            !GuestPhoneCall.Ringing || envelope_Type != Envelope_Type.Hydro_StartConstruction ||
            MonMPPlugin.Instance?.World?.Progress.RequestPhoneService(SharedProgress.PhoneService.HydroStartLetter) != true;
    }
}
