using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    internal static class TirePumpPatchAccess
    {
        internal static bool Down(InteractableTools tool, TirePumpAction action) =>
            SharedTirePressure.Current?.InterceptMouseDown(tool, action) ?? true;
        internal static bool Up(InteractableTools tool, TirePumpAction action) =>
            SharedTirePressure.Current?.InterceptMouseUp(tool, action) ?? true;
    }

    [HarmonyPatch(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseDown))]
    internal static class SharedTirePumpMouseDownPatch
    { private static bool Prefix(Tool_TirePump __instance) => TirePumpPatchAccess.Down(__instance, TirePumpAction.Inflate); }
    [HarmonyPatch(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseUp))]
    internal static class SharedTirePumpMouseUpPatch
    { private static bool Prefix(Tool_TirePump __instance) => TirePumpPatchAccess.Up(__instance, TirePumpAction.Inflate); }
    [HarmonyPatch(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseRightDown))]
    internal static class SharedTirePumpMouseRightDownPatch
    { private static bool Prefix(Tool_TirePump __instance) => TirePumpPatchAccess.Down(__instance, TirePumpAction.Deflate); }
    [HarmonyPatch(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseRightUp))]
    internal static class SharedTirePumpMouseRightUpPatch
    { private static bool Prefix(Tool_TirePump __instance) => TirePumpPatchAccess.Up(__instance, TirePumpAction.Deflate); }

    [HarmonyPatch(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseDown))]
    internal static class SharedAirTirePumpMouseDownPatch
    { private static bool Prefix(Tool_Air_TirePump __instance) => TirePumpPatchAccess.Down(__instance, TirePumpAction.Inflate); }
    [HarmonyPatch(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseUp))]
    internal static class SharedAirTirePumpMouseUpPatch
    { private static bool Prefix(Tool_Air_TirePump __instance) => TirePumpPatchAccess.Up(__instance, TirePumpAction.Inflate); }
    [HarmonyPatch(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseRightDown))]
    internal static class SharedAirTirePumpMouseRightDownPatch
    { private static bool Prefix(Tool_Air_TirePump __instance) => TirePumpPatchAccess.Down(__instance, TirePumpAction.Deflate); }
    [HarmonyPatch(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseRightUp))]
    internal static class SharedAirTirePumpMouseRightUpPatch
    { private static bool Prefix(Tool_Air_TirePump __instance) => TirePumpPatchAccess.Up(__instance, TirePumpAction.Deflate); }

    [HarmonyPatch(typeof(Part_Driveline_Wheel), "set_Value1")]
    internal static class SharedTirePressureValuePatch
    {
        private static bool Prefix(ref float value) =>
            SharedTirePressure.Current?.AllowNativePressureMutation(ref value) ?? true;
    }

    [HarmonyPatch(typeof(Part_Driveline_Wheel), nameof(Part_Driveline_Wheel.PopTheTire))]
    internal static class SharedTirePopPatch
    {
        private static bool Prefix() => SharedTirePressure.Current?.AllowNativeTirePop() ?? true;
    }

    [HarmonyPatch(typeof(AirCompressor), nameof(AirCompressor.UsingAir))]
    internal static class SharedCompressorConsumptionPatch
    {
        private static bool Prefix() => SharedTirePressure.Current?.AllowNativeCompressorUse() ?? true;
    }
    [HarmonyPatch(typeof(AirCompressor), "StartMotor")]
    internal static class SharedCompressorMotorAuthorityPatch
    {
        private static bool Prefix(AirCompressor __instance) => SharedTirePressure.Current?.AllowNativeCompressorMotor(__instance) ?? true;
    }
}
