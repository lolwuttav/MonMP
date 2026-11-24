using HarmonyLib;
using MonMP.Plugin.Replication;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Trailer;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Store_TowingService_Truck), nameof(Store_TowingService_Truck.MouseDown))]
    internal static class TowingServiceTruckPatch
    {
        private static bool Prefix(Store_TowingService_Truck __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (SharedTowingService.Active == null) return plugin.Session.IsHost;
            return SharedTowingService.Active.AllowTruckOpen(__instance);
        }
    }

    [HarmonyPatch(typeof(Store_TowingService), nameof(Store_TowingService.OpenTowingService))]
    internal static class TowingServiceOpenPatch
    {
        private static bool Prefix(Store_TowingService __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (SharedTowingService.Active == null) return plugin.Session.IsHost;
            return SharedTowingService.Active.AllowOpen(__instance);
        }
    }

    [HarmonyPatch(typeof(Store_TowingService), nameof(Store_TowingService.TowThisVehicle), new[] { typeof(Vehicle_Type) })]
    internal static class TowingServicePurchasePatch
    {
        private static bool Prefix(Store_TowingService __instance, Vehicle_Type vehicle_Type)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (SharedTowingService.Active == null) return plugin.Session.IsHost;
            return SharedTowingService.Active.InterceptTow(__instance, vehicle_Type);
        }

        private static void Postfix(Store_TowingService __instance) =>
            SharedTowingService.Active?.NativeTowCompleted(__instance);
    }

    [HarmonyPatch(typeof(Store_TowingService), "NewDay")]
    internal static class TowingServiceRecoveryPatch
    {
        private static bool Prefix(Store_TowingService __instance, out bool __state)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return __state = true;
            if (SharedTowingService.Active == null) return __state = plugin.Session.IsHost;
            return __state = SharedTowingService.Active.AllowNativeNewDay(__instance);
        }

        // Harmony runs postfixes after a skipping prefix too. A deferred recovery (the store opens at
        // 8:00, usually while a shared sleep or save blocks new actions) used to reach the completion
        // handler, which cancelled the retry and waited on a recovery that never started, so a
        // guest's queued tow never happened however many days passed (#123).
        private static void Postfix(Store_TowingService __instance, bool __state)
        {
            if (__state) SharedTowingService.Active?.NativeNewDayCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Store_TowingService), nameof(Store_TowingService.LoadFromSave), new[] { typeof(Vehicle_Type) })]
    internal static class TowingServiceLoadAuthorityPatch
    {
        private static bool Prefix(Store_TowingService __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            return SharedTowingService.Active?.AllowNativeLoad(__instance) ?? false;
        }
    }

    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.EnterExitVehicle))]
    [HarmonyPriority(Priority.First)]
    internal static class TowingServiceVehicleEntryGatePatch
    {
        private static bool Prefix(Gameplay __instance, Vehicle _vehicleController)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return SharedTowingService.Active?.AllowVehicleEntry(__instance, _vehicleController) ?? true;
        }
    }

    [HarmonyPatch(typeof(TrailerHitchModule), "AttachTrailer", new[] { typeof(VehicleController) })]
    [HarmonyPriority(Priority.First)]
    internal static class TowingServiceTrailerAttachGatePatch
    {
        private static bool Prefix(VehicleController vc)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return SharedTowingService.Active?.AllowTrailerAttach(vc) ?? true;
        }
    }
}
