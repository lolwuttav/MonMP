using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Tool_HoistPuller), nameof(Tool_HoistPuller.Crink))]
    internal static class SharedHoistNativeCrankPatch
    {
        private static readonly AccessTools.FieldRef<Tool_HoistPuller, float> Timer =
            AccessTools.FieldRefAccess<Tool_HoistPuller, float>("Timer_LastHoist");
        private static void Prefix(Tool_HoistPuller __instance, out float __state) => __state = Timer(__instance);
        private static void Postfix(Tool_HoistPuller __instance, float __state, bool __runOriginal)
        {
            if (__runOriginal) SharedHoistPullers.Current?.NativeCranked(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(PlayerInteractable), nameof(PlayerInteractable.Grab_HoistPuller), new[] { typeof(Tool_HoistPuller) })]
    internal static class SharedHoistNativePickupPatch
    {
        private static bool Prefix(Tool_HoistPuller _tool_HoistPuller) =>
            SharedHoistPullers.Current?.InterceptNativePickup(_tool_HoistPuller) != true;
    }

    [HarmonyPatch(typeof(Tool_HoistPuller_Handle), nameof(Tool_HoistPuller_Handle.MouseDown))]
    internal static class SharedHoistHandleCrankPatch
    {
        private static bool Prefix(Tool_HoistPuller_Handle __instance) =>
            SharedHoistPullers.Current?.InterceptHandle(__instance, true) != true;
    }

    [HarmonyPatch(typeof(Tool_HoistPuller_Handle), nameof(Tool_HoistPuller_Handle.MouseRightDown))]
    internal static class SharedHoistHandleRecoveryPatch
    {
        private static bool Prefix(Tool_HoistPuller_Handle __instance) =>
            SharedHoistPullers.Current?.InterceptHandle(__instance, false) != true;
    }

    [HarmonyPatch(typeof(Vehicle_HoistPullerBox), nameof(Vehicle_HoistPullerBox.MouseDown))]
    internal static class SharedHoistCasePickupPatch
    {
        private static bool Prefix(Vehicle_HoistPullerBox __instance) =>
            SharedHoistPullers.Current?.InterceptBox(__instance) != true;
    }

    // SharedItems' generic item lease hooks otherwise allow a guest to acquire/equip this custom
    // multi-stage tool, whose native placement coroutine has no remote-owner context.
    [HarmonyPatch(typeof(PlayerInteractable), "UseKeyDown")]
    internal static class SharedHoistUseOwnershipPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(InteractableItems ___HoldingItem, InteractableItems ___currentInteractableItem, InteractableTools ___activeTool, bool __runOriginal) =>
            __runOriginal && ((___HoldingItem == null && ___activeTool != null) ||
            SharedHoistPullers.Current?.InterceptToolPickup(___HoldingItem != null ? ___HoldingItem : ___currentInteractableItem) != true);
    }

    [HarmonyPatch(typeof(PlayerInteractable), "GrabInteractable")]
    internal static class SharedHoistGrabOwnershipPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(InteractableItems ___currentInteractableItem, bool __runOriginal) =>
            __runOriginal && SharedHoistPullers.Current?.InterceptToolGrab(___currentInteractableItem) != true;
    }

    [HarmonyPatch(typeof(Tool_HoistPuller), nameof(Tool_HoistPuller.MouseRightDown))]
    internal static class SharedHoistToolRecoveryPatch
    {
        private static bool Prefix(Tool_HoistPuller __instance) =>
            SharedHoistPullers.Current?.SuppressGuestToolRightClick(__instance) != true;
    }

    [HarmonyPatch(typeof(Tool_HoistPuller), "FixedUpdate")]
    internal static class SharedHoistReplicaPhysicsPatch
    {
        private static bool Prefix(Tool_HoistPuller __instance) =>
            SharedHoistPullers.Current?.SuppressReplicaPhysics(__instance) != true;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var native = AccessTools.Method(typeof(UnityEngine.Rigidbody), nameof(UnityEngine.Rigidbody.AddForceAtPosition),
                new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(UnityEngine.ForceMode) });
            var routed = AccessTools.Method(typeof(SharedHoistPullers), nameof(SharedHoistPullers.ApplyNativeForce));
            var result = new List<CodeInstruction>();
            int matches = 0;
            foreach (var instruction in instructions)
            {
                var copy = new CodeInstruction(instruction);
                if (instruction.Calls(native))
                {
                    copy.opcode = OpCodes.Call; copy.operand = routed; matches++;
                }
                result.Add(copy);
            }
            if (matches != 1) throw new System.InvalidOperationException("Unsupported native hoist force call layout.");
            return result;
        }
    }

    [HarmonyPatch]
    internal static class SharedHoistPlacementCoroutinePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(PlayerInteractable), "CheckForHoist")
            ?? throw new System.MissingMethodException("PlayerInteractable.CheckForHoist");

        private static bool Prefix(PlayerInteractable __instance, ref IEnumerator __result)
        {
            if (SharedHoistPullers.Current?.SuppressGuestHoistCoroutine(__instance) != true) return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty() { yield break; }
    }
}
