using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    // PoliceTicketData is saved in the host world. Radar and NPC offenses route to
    // the host; the guest copy must not independently create tickets or envelopes.
    [HarmonyPatch(typeof(Store_PoliceInterceptor), "NewTicketCreation", new[] { typeof(PoliceTicketData), typeof(Police_SpotDetails) })]
    internal static class PoliceTicketCreationGuardPatch
    {
        private static bool Prefix()
        {
            var session = MonMPPlugin.Instance?.Session;
            bool running = session?.IsRunning == true;
            bool isHost = running && session!.IsHost;
            return !PoliceTicketAuthorityPolicy.ShouldSuppressNativeCreation(running, isHost);
        }
    }
}
