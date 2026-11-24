using System;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(PlayerInteractable), "UseKeyDown")]
    internal static class SharedItemUsePatch
    {
        private static bool Prefix(PlayerInteractable __instance, InteractableItems ___HoldingItem, InteractableItems ___currentInteractableItem,
            bool __runOriginal, out InteractableItems? __state)
        {
            __state = null;
            // The game's Harmony still invokes this state-producing prefix after an earlier
            // specialized handler returned false. Do not run a second use/refusal/pack lease.
            if (!__runOriginal) return false;
            if (___HoldingItem == null && __instance.Get_ActiveTool != null) return true;
            var plugin = MonMPPlugin.Instance;
            var item = ___HoldingItem != null ? ___HoldingItem : ___currentInteractableItem;
            if (plugin?.Items?.IsAdoptionPending(item) == true) return false;
            if (plugin?.Items?.Handles(item) != true) return true;
            bool runNative = plugin.Items.Use(item);
            if (runNative && item is InteractablePacks) __state = item;
            return runNative;
        }
        private static Exception? Finalizer(Exception? __exception, InteractableItems? __state)
        {
            if (__state != null) MonMPPlugin.Instance?.Items?.EndNativeUse(__state);
            return __exception;
        }
    }
}
