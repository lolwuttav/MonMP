using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using NWH.DWP2.ShipController;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Boat_Anchor), "MouseDown")]
    internal static class SharedBoatAnchorMouseDownPatch
    {
        private static bool Prefix(Boat_Anchor __instance) => MonMPPlugin.Instance?.BoatControls?.AnchorClick(__instance) != false;
    }

    [HarmonyPatch(typeof(Boat_Anchor_FishingBoat), "MouseDown")]
    internal static class SharedFishingBoatAnchorMouseDownPatch
    {
        private static bool Prefix(Boat_Anchor_FishingBoat __instance) => MonMPPlugin.Instance?.BoatControls?.AnchorClick(__instance) != false;
    }

    [HarmonyPatch(typeof(Boat_Anchor), "MouseRightDown")]
    internal static class SharedBoatAnchorMouseRightDownPatch
    {
        private static bool Prefix(Boat_Anchor __instance) => MonMPPlugin.Instance?.BoatControls?.AnchorClick(__instance) != false;
    }

    [HarmonyPatch]
    internal static class SharedBoatAnchorMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Anchor), "Drop");
            yield return AccessTools.Method(typeof(Anchor), "Weigh");
        }

        private static bool Prefix(Anchor __instance, MethodBase __originalMethod) =>
            MonMPPlugin.Instance?.BoatControls?.AnchorInput(__instance, __originalMethod.Name == "Drop") != false;

        private static void Postfix(Anchor __instance) => MonMPPlugin.Instance?.BoatControls?.AnchorChanged(__instance);
    }

    [HarmonyPatch(typeof(Vehicle_IgnitionSwitch_FishingBoat), "MouseDown")]
    internal static class SharedFishingBoatIgnitionMouseDownPatch
    {
        private static bool Prefix(Vehicle_IgnitionSwitch_FishingBoat __instance) =>
            MonMPPlugin.Instance?.BoatControls?.IgnitionInput(__instance, true) != false;
    }

    [HarmonyPatch(typeof(Vehicle_IgnitionSwitch), "MouseDown")]
    internal static class SharedSmallCruiserIgnitionMouseDownPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Vehicle_IgnitionSwitch __instance)
        {
            // FishingBoat overrides MouseDown and already has its own purchase-gated route.
            if (__instance is Vehicle_IgnitionSwitch_FishingBoat) return true;
            var boat = __instance.GetComponentInParent<AdvancedShipController>();
            return boat?.vehicle_type != Vehicle_Type.SmallCruiser ||
                MonMPPlugin.Instance?.BoatControls?.IgnitionInput(__instance, true) != false;
        }
    }

    [HarmonyPatch(typeof(Vehicle_IgnitionSwitch), "MouseUp")]
    internal static class SharedBoatIgnitionMouseUpPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Vehicle_IgnitionSwitch __instance)
        {
            if (__instance is Vehicle_IgnitionSwitch_FishingBoat fishingIgnition)
                return MonMPPlugin.Instance?.BoatControls?.IgnitionInput(fishingIgnition, false) != false;
            var boat = __instance.GetComponentInParent<AdvancedShipController>();
            return boat?.vehicle_type != Vehicle_Type.SmallCruiser ||
                MonMPPlugin.Instance?.BoatControls?.IgnitionInput(__instance, false) != false;
        }
    }

    [HarmonyPatch]
    internal static class SharedSmallCruiserFuelCapPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Boat_SmallCruiser_FuelCap), "MouseDown");
            yield return AccessTools.Method(typeof(Boat_SmallCruiser_FuelCap), "MouseRightDown");
        }

        private static bool Prefix(Boat_SmallCruiser_FuelCap __instance) =>
            MonMPPlugin.Instance?.BoatControls?.FuelCapClick(__instance) != false;
    }

    [HarmonyPatch(typeof(Boat_SmallCruiser_FuelCap), "onLoadSaveSystem")]
    internal static class SharedSmallCruiserFuelCapLoadPatch
    {
        private static void Postfix(Boat_SmallCruiser_FuelCap __instance) =>
            MonMPPlugin.Instance?.BoatControls?.FuelCapLoaded(__instance);
    }
}
