using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH.WheelController3D;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(WheelController), nameof(WheelController.OnPhysicsSubstep), new[] { typeof(float), typeof(float), typeof(int) })]
    internal static class RemoteWheelPhysicsPatch
    {
        private static bool Prefix(WheelController __instance
#if MONMP_PERFORMANCE_CAPTURE
            , out Runtime.CaptureDetail.Scope __state
#endif
        )
        {
            bool suppress = RemoteWheelForces.Suppress(__instance);
#if MONMP_PERFORMANCE_CAPTURE
            __state = Runtime.CaptureDetail.Measure(suppress ? "NativeWheel.Physics.Suppressed" : "NativeWheel.Physics.Run");
#endif
            if (!suppress) return true;
            RemoteWheelForces.PresentRemoteStep(__instance);
            return false;
        }
#if MONMP_PERFORMANCE_CAPTURE
        private static void Finalizer(Runtime.CaptureDetail.Scope __state) => __state.Dispose();
#endif
    }

    [HarmonyPatch(typeof(WheelController), nameof(WheelController.OnPostPhysicsSubstep), new[] { typeof(float), typeof(float) })]
    internal static class RemoteWheelPostPhysicsPatch
    {
        private static bool Prefix(WheelController __instance
#if MONMP_PERFORMANCE_CAPTURE
            , out Runtime.CaptureDetail.Scope __state
#endif
        )
        {
            bool suppress = RemoteWheelForces.Suppress(__instance);
#if MONMP_PERFORMANCE_CAPTURE
            __state = Runtime.CaptureDetail.Measure(suppress ? "NativeWheel.Post.Cleanup" : "NativeWheel.Post.Run");
#endif
            if (!suppress) return true;
            RemoteWheelForces.FinishRemoteStep(__instance);
            return false;
        }
#if MONMP_PERFORMANCE_CAPTURE
        private static void Finalizer(Runtime.CaptureDetail.Scope __state) => __state.Dispose();
#endif
    }

#if MONMP_PERFORMANCE_CAPTURE
    // Separate detail rows are deliberately excluded from top-level scope sums.
    // Include the existing visual postfix in this invocation's elapsed time.
    [HarmonyPatch(typeof(WheelController), nameof(WheelController.OnPrePhysicsSubstep), new[] { typeof(float), typeof(float) })]
    internal static class NativeWheelPreCapturePatch
    {
        private static void Prefix(out Runtime.CaptureDetail.Scope __state) => __state = Runtime.CaptureDetail.Measure("NativeWheel.Pre.Run");
        private static void Finalizer(Runtime.CaptureDetail.Scope __state) => __state.Dispose();
    }
#endif
}
