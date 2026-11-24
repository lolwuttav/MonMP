using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // A guest's wrench or impact gun letting go of its bolt (a click, or a right-click throw) releases
    // the shared bolt right away, as a throw when native code flung it.
    [HarmonyPatch]
    internal static class BoltToolReleasePatch
    {
        private static readonly FieldInfo SpannerBolt = AccessTools.Field(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt")!;
        private static readonly FieldInfo ImpactBolt = AccessTools.Field(typeof(Tool_Air_Impact), "CurrentHolding_Bolt")!;

        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Tool_AdjustableSpanner), "CheckForPickingABolt", new[] { typeof(bool) })!;
            yield return AccessTools.Method(typeof(Tool_Air_Impact), "CheckForPickingABolt", new[] { typeof(bool) })!;
        }

        private static InteractableItems? Held(InteractableTools tool) =>
            (tool is Tool_AdjustableSpanner ? SpannerBolt : ImpactBolt).GetValue(tool) as InteractableItems;

        private static bool Prefix(InteractableTools __instance, bool trowHard, out InteractableItems? __state)
        {
            __state = Held(__instance);
            if (MonMPPlugin.Instance?.Items?.AllowToolPackPicking(__instance) != false) return true;
            // MouseDown sets this bit before CheckForPickingABolt; clear it along with suppressing
            // the native call so the leased pack cannot leave the tool stuck in a pressed state.
            MonMPPlugin.Instance?.World?.Bolts?.ClearPackWaitInput(__instance, trowHard);
            return false;
        }

        private static void Postfix(InteractableTools __instance, bool trowHard, InteractableItems? __state)
        {
            if (__state == null || Held(__instance) != null) return;
            MonMPPlugin.Instance?.Items?.ToolBoltReleased(__state, trowHard);
        }
    }

    // Vehicle_PartSocket.Detach is the wrench and impact gun's right-click shifter-knob removal. It
    // reports like RemovePart: the socket state goes to the host (which validates it, as for any
    // removal) and the knob becomes a shared loose item, so guests can remove knobs too (#292).
    [HarmonyPatch(typeof(Vehicle_PartSocket), nameof(Vehicle_PartSocket.Detach))]
    internal static class SharedVehiclePartDetachPatch
    {
        private static void Prefix(Vehicle_PartSocket __instance, out InteractableParts? __state) => __state = __instance.isInstalled ? __instance.partInstalled : null;
        private static void Postfix(Vehicle_PartSocket __instance, InteractableParts? __state) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyDetached(__instance, __state);
    }
}
