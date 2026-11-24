using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Envelope_Police_Ticket), nameof(Envelope_Police_Ticket.Equiped))]
    internal static class PoliceEnvelopeDisplayPatch
    {
        private static void Postfix(Envelope_Police_Ticket __instance, TMPro.TextMeshPro ___Text_TicketsList, TMPro.TextMeshPro ___BillToPay_Price) =>
            MonMPPlugin.Instance?.World?.Information.ShowPoliceLetter(__instance, ___Text_TicketsList, ___BillToPay_Price);
    }

    [HarmonyPatch(typeof(InteractableTools), nameof(InteractableTools.UnEquiped))]
    internal static class PoliceEnvelopeUnequipPatch
    {
        private static void Postfix(InteractableTools __instance)
        { if (__instance is Envelope_Police_Ticket letter) MonMPPlugin.Instance?.World?.Information.HidePoliceLetter(letter); }
    }

    // Native Start hides the signature after a new replica has already received IntValue1.
    [HarmonyPatch(typeof(Tool_Envelope), nameof(Tool_Envelope.Start))]
    internal static class EnvelopeSignatureStartPatch
    {
        private static void Postfix(Tool_Envelope __instance) =>
            MonMPPlugin.Instance?.Items?.RestoreEnvelopePresentation(__instance);
    }

    // A hydro letter entering the post box is a transaction, not an independent guest-side
    // cash or HydroData mutation. Guests route it to the host; the host keeps the vanilla path.
    [HarmonyPatch(typeof(Store_PostOffice), nameof(Store_PostOffice.TryingToSendThisLetter))]
    internal static class HydroEnvelopePostPatch
    {
        private static bool Prefix(Store_PostOffice __instance, Tool_Envelope envelope, out bool __state)
        {
            var mail = MonMPPlugin.Instance?.World?.HydroMail;
            __state = false;
            return mail == null || !mail.Intercept(__instance, envelope, out __state);
        }

        private static void Postfix(bool __state) => MonMPPlugin.Instance?.World?.HydroMail.NativeCallFinished(__state);
    }

    // A guest's signature on a held letter: the host signs its copy, or its snapshot erases it (#204).
    [HarmonyPatch(typeof(Tool_Envelope), nameof(Tool_Envelope.MouseDown))]
    internal static class HydroEnvelopeSignPatch
    {
        private static void Prefix(Tool_Envelope __instance, out bool __state) => __state = __instance.IntValue1 != 1;

        private static void Postfix(Tool_Envelope __instance, bool __state)
        {
            if (__state && __instance.IntValue1 == 1) MonMPPlugin.Instance?.World?.HydroMail.LocalSigned(__instance);
        }
    }
}
