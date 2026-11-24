using CMF;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Native chairs are static, so Gameplay.EnterExitChair parents the walker to the seat and never
    // resets its rotation on the way out. MonMP's rider/cab/bed seats move with their vehicle: the walker
    // turns and tilts with it, and after dismounting a turned or tilted vehicle the player kept that tilt,
    // and CMF gravity (along the walker's up axis) pulled them sideways until they sat in a driver seat,
    // whose native exit does reset the rotation (#111). The avatar others see used the same skewed
    // rotation, which also showed a host seated backwards on a bar stool afterwards (#129).
    [HarmonyPatch(typeof(AdvancedWalkerController), nameof(AdvancedWalkerController.ExitChair))]
    internal static class WalkerUprightPatch
    {
        private static void Prefix(AdvancedWalkerController __instance)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true) return;
            var walker = __instance.transform;
            // ForceExitChair (the native Stuck button) leaves the walker parented to the seat,
            // including when a level seat faces world zero and needs no rotation correction.
            if (walker.parent != null) walker.SetParent(null, true);
            if (Quaternion.Angle(walker.rotation, Quaternion.identity) < 0.5f) return;
            float yaw = __instance.cameraTransform != null ? __instance.cameraTransform.eulerAngles.y : walker.eulerAngles.y;
            walker.rotation = Quaternion.identity;
            // Keep looking where the player looked; ExitChair keeps this yaw and levels the pitch.
            __instance.playerCameraController?.SetRotationAngles(0f, yaw);
        }
    }
}
