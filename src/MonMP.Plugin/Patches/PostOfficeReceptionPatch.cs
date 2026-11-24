using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    // This native trigger only forwards envelopes into Store_PostOffice. Managed mail
    // continues into SharedHydroMail; nonpostable ads retain their harmless native error
    // message. Any new postable kind fails closed on guests until it receives a host route.
    [HarmonyPatch(typeof(PostOffice_ReceptionBox), "OnTriggerEnter", new[] { typeof(UnityEngine.Collider) })]
    internal static class PostOfficeReceptionPatch
    {
        private static bool Prefix(UnityEngine.Collider other)
        {
            if (other == null) return true;
            var envelope = other.GetComponent<Tool_Envelope>();
            return AllowLocalPost(envelope);
        }

        internal static bool AllowLocalPost(Tool_Envelope envelope)
        {
            var plugin = MonMPPlugin.Instance;
            var session = plugin?.Session;
            bool routeAvailable = plugin?.World?.HydroMail.CanRouteGuestPost == true;
            if (envelope == null) return true;
            var kind = KindOf(envelope.envelope_Type);
            if (!PostOfficeReceptionPolicy.ShouldSuppressLocalPost(
                session?.IsRunning == true, session?.IsHost == true, envelope.CanBePosted, kind, routeAvailable)) return true;
            string message = !PostOfficeReceptionPolicy.HasHostPostRoute(kind)
                ? "This letter type needs host-authoritative post handling before it can be mailed in multiplayer."
                : "Mail is not ready on the host yet. Wait until the multiplayer world is synchronized before posting it.";
            plugin?.ReportItemUseUnavailable(message);
            return false;
        }

        private static PostOfficeEnvelopeKind KindOf(Envelope_Type type) => type switch
        {
            Envelope_Type.Hydro_StartConstruction => PostOfficeEnvelopeKind.HydroConstruction,
            Envelope_Type.Hydro_Facture => PostOfficeEnvelopeKind.HydroBill,
            Envelope_Type.Police_Ticket => PostOfficeEnvelopeKind.PoliceTicket,
            _ => PostOfficeEnvelopeKind.Unknown
        };
    }

    // Guard the native transaction itself, not only the trigger that normally calls it.
    // Managed hydro and police letters pass through to SharedHydroMail; an unhandled
    // postable envelope cannot spend or advance state through a direct callback.
    [HarmonyPatch(typeof(Store_PostOffice), nameof(Store_PostOffice.TryingToSendThisLetter), new[] { typeof(Tool_Envelope) })]
    [HarmonyPriority(Priority.First)]
    internal static class PostOfficeSubmissionGuardPatch
    {
        private static bool Prefix(Tool_Envelope envelope) => PostOfficeReceptionPatch.AllowLocalPost(envelope);
    }
}
