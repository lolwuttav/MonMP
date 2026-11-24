using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // MapTorch reaches these methods directly before InteractableItems.StartFire. On a guest,
    // a shared paper is ignited by the host's copy instead: the paper's removal and whatever
    // it set alight come back through the shared records.
    [HarmonyPatch(typeof(Tool_Envelope), nameof(Tool_Envelope.StartTheFire))]
    internal static class SharedEnvelopeIgnitionPatch
    {
        private static bool Prefix(Tool_Envelope __instance) => SharedPaperIgnition.Allow(__instance);
        private static void Postfix(Tool_Envelope __instance) => SharedPaperIgnition.Burned(__instance);
    }

    [HarmonyPatch(typeof(Newspaper_Crumpled), nameof(Newspaper_Crumpled.StartTheFire))]
    internal static class SharedNewspaperIgnitionPatch
    {
        private static bool Prefix(Newspaper_Crumpled __instance) => SharedPaperIgnition.Allow(__instance);
        private static void Postfix(Newspaper_Crumpled __instance) => SharedPaperIgnition.Burned(__instance);
    }

    internal static class SharedPaperIgnition
    {
        internal static bool Allow(InteractableItems item)
        {
            var items = MonMPPlugin.Instance?.Items;
            if (items == null || !items.GuestRunning || !items.Handles(item)) return true;
            // A guest's presentation fire does not ask the host; the host's own fire reaches its paper.
            if (!SharedItems.GuestFireTicking) items.RequestIgnite(item);
            return false;
        }
        // Host: the paper burst natively (it is destroyed at frame end); publish it as burned.
        internal static void Burned(InteractableItems item)
        {
            var items = MonMPPlugin.Instance?.Items;
            if (items == null || items.GuestRunning || Singleton<Gameplay>.i.Player.IsHoldingThisObject(item)) return;
            items.NoteBurned(item);
        }
    }
}
