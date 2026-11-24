using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(PlayerInteractable), "GrabInteractable")]
    internal static class EasterEggPickupPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(InteractableItems ___currentInteractableItem)
        {
            if (!(___currentInteractableItem is Item_Event_EasterEgg egg)) return true;

            var plugin = MonMPPlugin.Instance;
            var items = plugin?.Items;
            var session = plugin?.Session;
            if (session?.IsRunning == true && session.IsHost && plugin!.World?.EasterEggs?.AlreadyConsumed(egg) == true)
                return false; // An already-selected native reference can survive until end-of-frame.
            if (items?.Handles(egg) != true || session?.IsRunning != true) return true;

            // Guest requests and local host finds share the same synchronous consumption path.
            if (plugin!.World?.EasterEggs?.RequestPickup(egg) == true) return false;
            plugin.ReportItemUseUnavailable("The Easter egg is not shared yet; try again in a moment.");
            return false;
        }
    }

    [HarmonyPatch(typeof(UI_GenericWindow_Gilles), "Button_FindThisObject")]
    internal static class EasterGillesSearchPatch
    {
        [HarmonyPriority(Priority.First)]
        // A guest's search, Easter basket and eggs included, is the host's shared request (SharedGilles, #291).
        private static bool Prefix(UI_GenericWindow_Gilles __instance, InteractableItems ___Object_Selected) =>
            MonMPPlugin.Instance?.World?.Gilles.Intercept(__instance, ___Object_Selected) != true;
    }

    // Host: Gilles returns the searched item at 18:00 (SharedGilles).
    [HarmonyPatch(typeof(Store_Gilles), "Tick")]
    internal static class GillesReturnTickPatch
    {
        private static void Prefix(int tick) => MonMPPlugin.Instance?.World?.Gilles.BeforeTick(tick);
        private static void Postfix() => MonMPPlugin.Instance?.World?.Gilles.AfterTick();
    }
}
