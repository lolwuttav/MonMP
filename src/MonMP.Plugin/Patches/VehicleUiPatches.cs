using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.VehicleGUI;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedRearToePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Vehicle_ToeManager), nameof(Vehicle_ToeManager.KnobToeAngle_RL))!;
            yield return AccessTools.Method(typeof(Vehicle_ToeManager), nameof(Vehicle_ToeManager.KnobToeAngle_RR))!;
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Vehicle_ToeManager __instance, int direction, MethodBase __originalMethod)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || SharedVehicleUi.IsApplying) return true;
            var ui = plugin.World?.VehicleUi;
            if (ui?.Ready != true) return false;
            if (plugin.Session.IsHost) return true;
            var side = __originalMethod.Name == nameof(Vehicle_ToeManager.KnobToeAngle_RL)
                ? VehicleToeSide.RearLeft : VehicleToeSide.RearRight;
            ui.RequestToe(__instance, side, direction);
            return false;
        }
    }

    [HarmonyPatch(typeof(DashGUIController), "Update")]
    internal static class SharedVehicleOdometerDisplayPatch
    {
        private static readonly FieldInfo? UpdateFlag = AccessTools.Field(typeof(DashGUIController), "_update");
        private static void Postfix(DashGUIController __instance)
        {
            if (__instance == null || !__instance.enabled || UpdateFlag?.GetValue(__instance) is not bool update || !update ||
                !__instance.useDigitalTotalKmGauge || __instance.digitalTotalKMGauge == null ||
                __instance.vehicleController == null || __instance.vehicleController.Get_IgnitionPosition <= 0) return;
            var ui = SharedVehicleUi.Active;
            if (ui == null || !ui.TryGetOdometer(__instance.vehicleController, out float km)) return;
            float display = __instance.digitalTotalKMUseMPH ? km * 0.6213712f : km;
            __instance.digitalTotalKMGauge.stringValue = display.ToString("0");
        }
    }
}
