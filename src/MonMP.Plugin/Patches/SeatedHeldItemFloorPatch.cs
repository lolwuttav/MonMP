using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(PlayerInteractable), "KeepHoldingItemAboveFloor")]
    internal static class SeatedHeldItemFloorPatch
    {
        private static void Prefix(Gameplay ___gameplay, InteractableItems ___HoldingItem, ref float ___PlayerHeight,
            out float? __state)
        {
            __state = null;
            if (___gameplay == null || !SeatedHeldItemFloor.TryRetainHeight(MonMPPlugin.Instance?.Session?.IsRunning == true,
                ___gameplay.Get_PlayerState, ___HoldingItem, ___PlayerHeight, out float floorHeight)) return;
            __state = ___PlayerHeight;
            ___PlayerHeight = floorHeight;
        }

        // PlayerHeight remains the native ground sample for the next tick/bunker calculation.
        // A finalizer also restores it if the native floor/grid presentation throws.
        private static Exception? Finalizer(Exception? __exception, ref float ___PlayerHeight, float? __state)
        {
            if (__state.HasValue) ___PlayerHeight = __state.Value;
            return __exception;
        }
    }
}
