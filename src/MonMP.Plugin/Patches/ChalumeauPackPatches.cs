using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Route the exact guest hand-use path before SharedItems can predict a native pack decrement.
    // Other packs, host use and offline use continue through their existing handlers.
    [HarmonyPatch(typeof(PlayerInteractable), "UseKeyDown")]
    internal static class SharedChalumeauPackUsePatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(PlayerInteractable __instance, InteractableItems ___HoldingItem,
            InteractableItems ___currentInteractableItem, bool __runOriginal) =>
            __runOriginal && !(SharedChalumeauPack.Active?.InterceptUse(__instance, ___HoldingItem, ___currentInteractableItem) ?? false);
    }
}
