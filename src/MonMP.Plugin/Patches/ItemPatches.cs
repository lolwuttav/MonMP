using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(PlayerInteractable), "GrabInteractable")]
    internal static class SharedItemGrabPatch
    {
        private static bool Prefix(InteractableItems ___currentInteractableItem, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var items = MonMPPlugin.Instance?.Items;
            return ___currentInteractableItem == null || items == null || items.Grab(___currentInteractableItem);
        }
    }
    [HarmonyPatch(typeof(PlayerInteractable), "UnGrabInteractable")]
    internal static class SharedItemDropPatch
    {
        private static void Postfix(InteractableItems? __state)
        {
            if (__state != null) Replication.NativeItemDiscovery.Changed(__state);
        }
        private static bool Prefix(InteractableItems ___HoldingItem, bool trowHard, out InteractableItems? __state)
        {
            __state = ___HoldingItem;
            var items = MonMPPlugin.Instance?.Items;
            if (___HoldingItem == null || items == null) return true;
            bool runNative = items.Drop(___HoldingItem, trowHard);
            if (!runNative && !trowHard) MonMPPlugin.Instance?.World?.TrashRecycle.RecordDrop(___HoldingItem);
            return runNative;
        }
    }
    // Native mouse handlers bypass UseKeyDown and dispatch directly to the equipped tool.
    // An equip may be pending host approval, or the lease may have been released while
    // activeTool still points at the old item. Block that dispatch and clear button-up state.
    [HarmonyPatch]
    internal static class SharedActiveToolMousePatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            foreach (string name in new[] { "MouseLeftClickDown", "MouseLeftClickUp", "MouseRightClickDown", "MouseRightClickUp" })
            {
                var method = AccessTools.Method(typeof(PlayerInteractable), name, Type.EmptyTypes);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported native tool input dispatch: " + name);
                yield return method;
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(InteractableTools ___activeTool, ref ToolsType ___activeToolType, MethodBase __originalMethod,
            ref bool ___isMouseLeftClickDown, ref bool ___isMouseRightClickDown)
        {
            var items = MonMPPlugin.Instance?.Items;
            // Native RightDown/RightUp can call activeTool without a null check when only the
            // tool type is left behind. Repair this guest-only transient before dispatch.
            if (___activeTool == null && items?.GuestRunning == true && ___activeToolType != ToolsType.Useless)
                ___activeToolType = ToolsType.Useless;
            if (items?.AllowActiveToolInput(___activeTool) != false) return true;
            if (__originalMethod.Name == "MouseLeftClickDown" || __originalMethod.Name == "MouseLeftClickUp")
                ___isMouseLeftClickDown = false;
            else
                ___isMouseRightClickDown = false;
            return false;
        }
    }
    [HarmonyPatch(typeof(InteractableItems), nameof(InteractableItems.StartFire))]
    internal static class SharedItemFirePatch
    {
        // The host burns shared items and replicates their removal; guest replicas never ignite locally.
        private static bool Prefix(InteractableItems __instance) => MonMPPlugin.Instance?.Items?.AllowFire(__instance) ?? true;
    }
    // Wrench and impact gun grip loose bolts with their own GrabBolt; a shared bolt needs its lease first.
    [HarmonyPatch]
    internal static class SharedToolGrabPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Tool_AdjustableSpanner), "GrabBolt");
            yield return AccessTools.Method(typeof(Tool_Air_Impact), "GrabBolt");
        }
        private static bool Prefix(InteractableTools __instance, Hardware_Bolt bolt) =>
            bolt == null || MonMPPlugin.Instance?.Items?.ToolGrab(__instance, bolt) != false;
        private static void Postfix(InteractableTools __instance, Hardware_Bolt bolt) =>
            MonMPPlugin.Instance?.Items?.ToolGrabCompleted(__instance, bolt);
    }
    // Each native screw step of a cap; a guest's turning of a shared canister's cap is sent to the host (#179).
    [HarmonyPatch(typeof(Part_FuelTankCap), "UpdateVisual")]
    internal static class SharedCanisterCapPatch
    {
        private static void Postfix(Part_FuelTankCap __instance) => MonMPPlugin.Instance?.Items?.CanisterCapTurned(__instance);
    }
    // A guest taking an item out of a shared pack by hand via Use.
    [HarmonyPatch]
    internal static class SharedPackUnpackPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InteractablePacks), nameof(InteractablePacks.Use));
        }
        private static void Prefix(InteractablePacks __instance, out float __state) => __state = __instance.Value1;
        private static void Postfix(InteractablePacks __instance, float __state)
        {
            if (__instance != null && __instance.Value1 < __state) MonMPPlugin.Instance?.Items?.Unpacked(__instance);
        }
    }
    // Wrench and impact-gun extraction calls GetOnePackedItem before a loose bolt has
    // a shared identity, so it cannot wait for that bolt's own lease.
    [HarmonyPatch(typeof(InteractablePacks), nameof(InteractablePacks.GetOnePackedItem))]
    internal static class SharedPackToolUnpackPatch
    {
        private static bool Prefix(InteractablePacks __instance, ref UnityEngine.GameObject? __result, out float __state)
        {
            __state = __instance.Value1;
            if (MonMPPlugin.Instance?.Items?.AllowToolUnpack(__instance) != false) return true;
            __result = null;
            return false;
        }
        private static void Postfix(InteractablePacks __instance, float __state, UnityEngine.GameObject? __result)
        {
            if (__instance != null) MonMPPlugin.Instance?.Items?.NativePackedItemExtracted(__instance, __result, __state);
        }
    }
}
