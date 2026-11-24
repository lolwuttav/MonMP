using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Store_PoliceInterceptor), "NewTicketCreation", new[] { typeof(PoliceTicketData), typeof(Police_SpotDetails) })]
    internal static class PoliceFinePresentationPatch
    {
        private static void Prefix(Store_PoliceInterceptor __instance, PoliceTicketData _ticketData, out NativePoliceFinePresentation.Capture? __state) =>
            __state = NativePoliceFinePresentation.Before(__instance, _ticketData);

        private static void Postfix(Store_PoliceInterceptor __instance, PoliceTicketData _ticketData, NativePoliceFinePresentation.Capture? __state) =>
            __state?.Finish(__instance, _ticketData);

        private static Exception? Finalizer(Store_PoliceInterceptor __instance, PoliceTicketData _ticketData,
            NativePoliceFinePresentation.Capture? __state, Exception? __exception)
        {
            __state?.Finish(__instance, _ticketData);
            return __exception;
        }
    }
}
