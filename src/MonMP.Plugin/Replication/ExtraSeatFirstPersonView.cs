using CMF;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class ExtraSeatFirstPersonView
    {
        internal static void Enter(AdvancedWalkerController walker, Transform seatLocation)
        {
            // Native EnterVehicle aligns the walker with the seat and clears camera-local angles.
            // EnterExitChair keeps the walker's preceding world rotation and supplies world Euler
            // camera angles instead. Use the vehicle path for added seats so first-person orientation
            // and offsets do not depend on an earlier walker/cinematic-camera state.
            walker.transform.SetPositionAndRotation(seatLocation.position, seatLocation.rotation);
            walker.CarCameraPosition(null);
        }
    }
}
