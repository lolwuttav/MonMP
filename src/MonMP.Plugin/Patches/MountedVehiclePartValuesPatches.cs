using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    // Keep native local feedback and achievements. Only a currently mounted, bound subtype
    // publishes its resulting socket state; loose parts remain owned by SharedItems.
    [HarmonyPatch]
    internal static class SharedCoiloverValuesPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "Knob_Height", "Knob_SpringForce", "Knob_Damper", "Knob_Rebound" })
                yield return AccessTools.Method(typeof(Part_Shock_Coilover), name, new[] { typeof(int) })
                    ?? throw new MissingMethodException(typeof(Part_Shock_Coilover).FullName, name);
        }
        private static void Prefix(Part_Shock_Coilover __instance, out MountedPartTuning __state) =>
            __state = new MountedPartTuning(__instance.Value1, __instance.Value2, __instance.IntValue1, __instance.IntValue2);

        private static void Postfix(Part_Shock_Coilover __instance, MountedPartTuning __state)
        {
            var plugin = MonMPPlugin.Instance;
            plugin?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
            plugin?.Items?.NotifyLooseSuspensionValuesChanged(__instance, MountedVehiclePartKind.Coilover, __state);
        }
    }

    [HarmonyPatch(typeof(Part_ControlArm_Tuned), nameof(Part_ControlArm_Tuned.Knob_Camber))]
    internal static class SharedTunedControlArmValuesPatch
    {
        private static void Prefix(Part_ControlArm_Tuned __instance, out MountedPartTuning __state) =>
            __state = new MountedPartTuning(__instance.Value1, 0, 0, 0);

        private static void Postfix(Part_ControlArm_Tuned __instance, MountedPartTuning __state)
        {
            var plugin = MonMPPlugin.Instance;
            plugin?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
            plugin?.Items?.NotifyLooseSuspensionValuesChanged(__instance, MountedVehiclePartKind.TunedControlArm, __state);
        }
    }

    [HarmonyPatch]
    internal static class SharedRearSeatValuesPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "Fold", "ForceUnfold" })
                yield return AccessTools.Method(typeof(Part_Seat_Rear), name, Type.EmptyTypes)
                    ?? throw new MissingMethodException(typeof(Part_Seat_Rear).FullName, name);
        }
        private static void Postfix(Part_Seat_Rear __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }

    [HarmonyPatch(typeof(Part_Autometer_RPM), nameof(Part_Autometer_RPM.Knob_ChangeShiftLightRPM))]
    internal static class SharedRpmGaugeValuesPatch
    {
        private static void Postfix(Part_Autometer_RPM __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }

    [HarmonyPatch(typeof(Part_Autometer_Speed), nameof(Part_Autometer_Speed.Change_UnitOfSpeed))]
    internal static class SharedSpeedGaugeValuesPatch
    {
        private static void Postfix(Part_Autometer_Speed __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }

    [HarmonyPatch(typeof(Part_Engine_ExhaustManifold_Turbo), nameof(Part_Engine_ExhaustManifold_Turbo.Knob_ChangeWestgateSound))]
    internal static class SharedTurboWastegateValuesPatch
    {
        private static void Postfix(Part_Engine_ExhaustManifold_Turbo __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }

    // Keep each native Grab path intact; its IntValue2 setter records the permanent one-time
    // history bit, and a mounted socket observes that resulting state after the interaction.
    [HarmonyPatch(typeof(Part_Suspension_Hub_Stake), nameof(Part_Suspension_Hub_Stake.Grab))]
    internal static class SharedStakePickupHistoryPatch
    {
        private static void Postfix(Part_Suspension_Hub_Stake __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }

    [HarmonyPatch(typeof(Part_Spoiler), nameof(Part_Spoiler.Grab))]
    internal static class SharedSpoilerPickupHistoryPatch
    {
        private static void Postfix(Part_Spoiler __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyMountedValuesChanged(__instance);
    }
}
