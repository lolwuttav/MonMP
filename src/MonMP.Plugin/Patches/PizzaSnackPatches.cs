using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Capture the target ray before SharedItems defers a guest's pickup until the
    // host grants the box lease. Vanilla grab still runs unchanged after approval.
    [HarmonyPatch(typeof(PlayerInteractable), "GrabInteractable")]
    internal static class SharedPizzaSnackGrabRayPatch
    {
        private static void Prefix(PlayerInteractable __instance, InteractableItems ___currentInteractableItem) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.CapturePickupRay(__instance, ___currentInteractableItem);
    }

    [HarmonyPatch(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.StartEvent))]
    internal static class SharedPizzaSnackStartPatch
    {
        private static bool Prefix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.StartEvent(__instance) != true;
        private static void Postfix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventChanged(__instance);
    }

    [HarmonyPatch(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PlayerStepIn))]
    internal static class SharedPizzaSnackStepInPatch
    {
        private static bool Prefix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.PlayerStepIn(__instance) != true;
        private static void Postfix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventChanged(__instance);
    }

    [HarmonyPatch(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PizzaDelivered))]
    internal static class SharedPizzaSnackDeliveredPatch
    {
        private static bool Prefix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.SuppressGuestEventMutation(__instance) != true;
        private static void Postfix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativePizzaDelivered(__instance);
    }

    // The authoritative native result posts one presentation event. Route snapshots
    // remain silent, including repair and late join; a timeout has its own exact cue.
    [HarmonyPatch(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.EventCompleted))]
    internal static class SharedPizzaSnackCompletedPresentationPatch
    {
        private static void Prefix(Event_PizzaSnack __instance, out bool __state) =>
            __state = MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventRunning(__instance) == true;
        private static void Postfix(Event_PizzaSnack __instance, bool __state) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventCompleted(__instance, __state);
    }

    [HarmonyPatch(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.CloseStore))]
    internal static class SharedPizzaSnackClosedPresentationPatch
    {
        private static void Prefix(Event_PizzaSnack __instance, out bool __state) =>
            __state = MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventRunning(__instance) == true;
        private static void Postfix(Event_PizzaSnack __instance, bool __state) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativeStoreClosed(__instance, __state);
    }

    // Store lifecycle and door methods alter event-owned fields or spawn boxes.
    // The host publishes those effects; guests apply the compact scene-indexed state.
    [HarmonyPatch]
    internal static class SharedPizzaSnackStateMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "OpenStore", "CloseStore", "OpenTakeOutDoor", "CloseTakeOutDoor", "EventCompleted" })
            {
                var method = AccessTools.Method(typeof(Event_PizzaSnack), name, System.Type.EmptyTypes);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new System.InvalidOperationException("Unsupported PizzaSnack state method: " + name);
                yield return method;
            }
        }
        private static bool Prefix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.SuppressGuestEventMutation(__instance) != true;
        private static void Postfix(Event_PizzaSnack __instance) =>
            MonMPPlugin.Instance?.World?.PizzaSnack?.NativeEventChanged(__instance);
    }

    [HarmonyPatch(typeof(Event_PizzaSnack_DropOffCube), "OnTriggerEnter", new[] { typeof(Collider) })]
    internal static class SharedPizzaSnackDropOffPatch
    {
        private static bool Prefix(Event_PizzaSnack_DropOffCube __instance, Collider other)
        {
            var adapter = MonMPPlugin.Instance?.World?.PizzaSnack;
            if (adapter == null) return true;
            if (adapter.GuestRunning) return !adapter.DropOffTrigger(__instance, other);
            return adapter.AllowNativeDropOff(__instance, other);
        }
    }
}
