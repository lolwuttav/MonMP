using System;
using HarmonyLib;
using Leo;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Vehicle_PartSocket), nameof(Vehicle_PartSocket.CheckIfCanToRemoved))]
    internal static class SharedDogBedRemovalEligibilityPatch
    {
        private static bool Prefix(Vehicle_PartSocket __instance, ref bool __result)
        {
            var shared = MonMPPlugin.Instance?.World?.Leo;
            if (shared == null || !shared.TryGuestDogBedRemoval(__instance, out bool allowed)) return true;
            __result = allowed;
            return false;
        }
    }

    // CarBed has a left-click action but inherits an empty right-click action.
    // Native input otherwise sends RMB there before trying its PartSocket ray.
    [HarmonyPatch(typeof(PlayerInteractable), "MouseRightClickDown")]
    internal static class SharedDogBedRemovalInputPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(PlayerInteractable __instance, ref Interactable? ___currentInteractable,
            InteractableItems? ___HoldingItem, InteractableTools? ___activeTool, ToolsType ___activeToolType,
            out CarBed? __state)
        {
            __state = null;
            if (MonMPPlugin.Instance?.Session?.IsRunning != true || ___HoldingItem != null || ___activeTool != null ||
                ___activeToolType != ToolsType.Useless || ___currentInteractable is not CarBed bed ||
                !TargetsInstalledBed(__instance.mainCamera, bed)) return;
            __state = bed;
            ___currentInteractable = null;
            // Run the original method: native game/input guards, held-button state,
            // removal eligibility, timer and synchronized output hooks all remain.
        }

        internal static bool TargetsInstalledBed(Camera? camera, CarBed bed)
        {
            if (camera == null || bed.Get_Vehicle == null) return false;
            var socket = bed.Get_Vehicle.vehicle_PartsManager?.DogBed;
            if (socket == null || !socket.isInstalled || socket.partInstalled is not Part_DogBed ||
                socket.CustomObjectToEnableWhenPartIsInstalled?.GetComponent<CarBed>() != bed) return false;
            var ray = MonMP.Plugin.Integration.LocalInteraction.SelectionRay(camera);
            int interactionMask = ~LayerMask.GetMask("Player", "PartSocket", "Ignore Raycast", "MainVehicleTrigger",
                "ContainerHole", "SwimmingPlayerSurface", "IgnoreByPlayerInteractable");
            // Recheck the current ray so a stale hover cannot reach through a door
            // or a different interaction. Require the exact nearest native socket.
            return Physics.Raycast(ray, out var hover, 2.4f, interactionMask) && hover.collider.GetComponent<CarBed>() == bed &&
                Physics.Raycast(ray, out var part, 2.4f, LayerMask.GetMask("PartSocket")) &&
                part.collider.GetComponent<Generated_Part>()?.carPartSocket == socket;
        }

        private static void Postfix(ref Interactable? ___currentInteractable, CarBed? __state) =>
            RestoreHover(ref ___currentInteractable, __state);

        private static Exception? Finalizer(Exception? __exception, ref Interactable? ___currentInteractable, CarBed? __state)
        {
            RestoreHover(ref ___currentInteractable, __state);
            return __exception;
        }

        private static void RestoreHover(ref Interactable? current, CarBed? previous)
        {
            if (current == null && previous != null) current = previous;
        }
    }
}
