using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class SeatedHeldItemFloor
    {
        internal static bool TryRetainHeight(bool multiplayerRunning, PlayerState state, InteractableItems? item,
            float groundHeight, out float floorHeight)
        {
            floorHeight = groundHeight;
            if (!multiplayerRunning || (state != PlayerState.InVehicle_Driver && state != PlayerState.InVehicle_Passenger &&
                state != PlayerState.InFurnitureChair && state != PlayerState.InCreeper) || item == null || item.meshRenderer == null)
                return false;

            // Native KeepHoldingItemAboveFloor also lowers objects toward the world ground until
            // their view-local center reaches 0.2. A seat/creeper view can be elevated or tilted,
            // so that world-Y move changes the picked-up pose and can carry it behind the camera.
            // Keep the existing hold height while retaining the native upward floor correction.
            var bounds = item.meshRenderer.bounds;
            float bottom = (bounds.center - (bounds.center - bounds.min)).y;
            if (!(bottom > groundHeight)) return false;
            floorHeight = bottom;
            return true;
        }
    }
}
