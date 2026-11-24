using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Furniture_CarLift), nameof(Furniture_CarLift.FreezeUnfreezeVehicleOnLift))]
    internal static class CarLiftFrozenVehiclePatch
    {
        private static void Postfix(Furniture_CarLift __instance) =>
            MonMPPlugin.Instance?.World?.Vehicles?.ObserveCarLift(__instance);
    }

    // A guest's lift buttons and arm clicks become host commands; the host runs the hydraulics.
    [HarmonyPatch(typeof(Furniture_CarLift), nameof(Furniture_CarLift.LiftButtonPressed))]
    internal static class CarLiftPressPatch
    {
        private static bool Prefix(Furniture_CarLift __instance, bool isLifting) =>
            MonMPPlugin.Instance?.Items?.LiftInput(__instance, new LiftCommand(isLifting ? LiftOp.Up : LiftOp.Down)) != false;
    }
    [HarmonyPatch(typeof(Furniture_CarLift), nameof(Furniture_CarLift.LiftButtonReleased))]
    internal static class CarLiftReleasePatch
    {
        private static bool Prefix(Furniture_CarLift __instance) => MonMPPlugin.Instance?.Items?.LiftInput(__instance, new LiftCommand(LiftOp.Stop)) != false;
    }
    [HarmonyPatch(typeof(Furniture_CarLift), nameof(Furniture_CarLift.MoveArms))]
    internal static class CarLiftArmPatch
    {
        private static bool Prefix(Furniture_CarLift __instance, int _direction, int _armID) =>
            _direction == 0 || _armID < 0 || _armID > 3 ||
            MonMPPlugin.Instance?.Items?.LiftInput(__instance, new LiftCommand(LiftOp.Arm, (byte)_armID, (sbyte)(_direction > 0 ? 1 : -1))) != false;
    }
    // The hold-to-unfreeze control calls the lift's Use directly; route it through the shared freeze toggle.
    [HarmonyPatch(typeof(Furniture_CarLift), nameof(Furniture_CarLift.ForceUnfreeze))]
    internal static class CarLiftUnfreezePatch
    {
        private static bool Prefix(Furniture_CarLift __instance)
        {
            var items = MonMPPlugin.Instance?.Items;
            return items?.Handles(__instance) != true || items.Use(__instance);
        }
    }
}
