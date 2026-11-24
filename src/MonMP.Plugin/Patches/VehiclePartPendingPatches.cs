using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedPendingPartValuesPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var target in new[] {
                (typeof(Part_Shock_Coilover), "Knob_Height"), (typeof(Part_Shock_Coilover), "Knob_SpringForce"),
                (typeof(Part_Shock_Coilover), "Knob_Damper"), (typeof(Part_Shock_Coilover), "Knob_Rebound"),
                (typeof(Part_ControlArm_Tuned), "Knob_Camber"), (typeof(Part_Seat_Rear), "Fold"), (typeof(Part_Seat_Rear), "ForceUnfold"),
                (typeof(Part_Autometer_RPM), "Knob_ChangeShiftLightRPM"), (typeof(Part_Autometer_Speed), "Change_UnitOfSpeed"),
                (typeof(Part_Engine_ExhaustManifold_Turbo), "Knob_ChangeWestgateSound"),
                (typeof(Part_SteeringRack), "KnobToeAngle_FL"), (typeof(Part_SteeringRack), "KnobToeAngle_FR"),
                (typeof(Part_SteeringRack), "KnobAdjustMaxSteerAngle"), (typeof(Part_SteeringRack), "KnobAdjustSteerSpeed") })
                yield return AccessTools.Method(target.Item1, target.Item2)
                    ?? throw new MissingMethodException(target.Item1.FullName, target.Item2);
        }
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(InteractableParts __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.AllowPartValues(__instance) != false;
    }

    // Guard the native input callers: suppressing InstallPart/RemovePart alone still lets
    // PlayerInteractable clear HoldingItem or grab the unchanged installed socket part.
    [HarmonyPatch(typeof(PlayerInteractable), "UnGrabInteractable")]
    internal static class SharedPendingPartInstallPatch
    {
        private static readonly FieldInfo GeneratedPart = AccessTools.Field(typeof(PlayerInteractable), "currentGeneratedPart")
            ?? throw new MissingFieldException(typeof(PlayerInteractable).FullName, "currentGeneratedPart");
        private static readonly FieldInfo GeneratedBolt = AccessTools.Field(typeof(PlayerInteractable), "Current_GeneratedBoltHole")
            ?? throw new MissingFieldException(typeof(PlayerInteractable).FullName, "Current_GeneratedBoltHole");
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(PlayerInteractable __instance)
        {
            if (__instance.Get_HoldingItem is Hardware_Bolt bolt && GeneratedBolt.GetValue(__instance) is Generated_BoltHole hole)
                return MonMPPlugin.Instance?.Items?.IsExactNativeOutputPending(bolt) != true &&
                    MonMPPlugin.Instance?.World?.Bolts.AllowLocalMount(hole.boltHoleDetails) != false;
            if (__instance.Get_HoldingItem is not InteractableParts part || GeneratedPart.GetValue(__instance) is not Generated_Part target) return true;
            return MonMPPlugin.Instance?.World?.VehicleParts.AllowPartChange(target.carPartSocket, part) != false;
        }
    }

    [HarmonyPatch]
    internal static class SharedPendingToolBoltInstallPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(Tool_AdjustableSpanner), typeof(Tool_Air_Impact) })
                yield return AccessTools.Method(type, "Spanner", Type.EmptyTypes) ?? throw new MissingMethodException(type.FullName, "Spanner");
        }
        private static void Postfix(InteractableTools __instance, ref IEnumerator __result) => __result = Guard(__instance, __result);
        private static IEnumerator Guard(InteractableTools tool, IEnumerator native)
        {
            var grip = AccessTools.Field(tool.GetType(), "CurrentHolding_Bolt") ?? throw new MissingFieldException(tool.GetType().FullName, "CurrentHolding_Bolt");
            try
            {
                while (true)
                {
                    if (grip.GetValue(tool) is Hardware_Bolt bolt && MonMPPlugin.Instance?.Items?.IsExactNativeOutputPending(bolt) == true)
                    { yield return null; continue; }
                    if (!native.MoveNext()) yield break;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }

    [HarmonyPatch(typeof(PlayerInteractable), "RemovePart_Timer")]
    internal static class SharedPendingPartRemovalPatch
    {
        private static readonly FieldInfo Crosshair = AccessTools.Field(typeof(PlayerInteractable), "ui_Crosshair")
            ?? throw new MissingFieldException(typeof(PlayerInteractable).FullName, "ui_Crosshair");
        private static void Postfix(PlayerInteractable __instance, Vehicle_PartSocket details, ref IEnumerator __result) =>
            __result = Guard(__instance, details, __result);
        private static IEnumerator Guard(PlayerInteractable player, Vehicle_PartSocket socket, IEnumerator native)
        {
            try
            {
                while (MonMPPlugin.Instance?.World?.VehicleParts.AllowPartChange(socket) != false)
                {
                    if (!native.MoveNext()) yield break;
                    yield return native.Current;
                }
                if (Crosshair.GetValue(player) is UI_Crosshair crosshair) crosshair.RemoveAPart(-1f);
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
    }
}
