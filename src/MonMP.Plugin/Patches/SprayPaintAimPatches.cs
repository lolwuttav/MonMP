using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(InteractableTools), "get_isCanInteractWhileHolding")]
    internal static class SprayPaintToolPriorityPatch
    {
        private static void Postfix(InteractableTools __instance, ref bool __result)
        {
            var plugin = MonMPPlugin.Instance;
            if (__instance is Tool_SprayPaintCan && plugin?.Session?.IsRunning == true)
                __result &= SprayPaintRaycast.CanUseWithCan(plugin.Gameplay?.Player?.Get_CurrentInteractable);
        }
    }

    // Keep native preview/host painting and the authoritative guest ray on the same
    // obstruction policy. No item/vehicle colliders are disabled to make a ray pass.
    [HarmonyPatch]
    internal static class SprayPaintPreviewRayPatch
    {
        private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(
            AccessTools.Method(typeof(Tool_SprayPaintCan), "CheckActivePannel")) ??
            throw new InvalidOperationException("Unsupported native spray preview coroutine.");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var native = AccessTools.Method(typeof(Physics), nameof(Physics.Raycast),
                new[] { typeof(Ray), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });
            var replacement = AccessTools.Method(typeof(SprayPaintPreviewRayPatch), nameof(Raycast));
            int replaced = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(native)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; replaced++; }
            }
            if (replaced != 1) throw new InvalidOperationException("Native spray preview ray signature changed.");
            return code;
        }

        private static bool Raycast(Ray ray, out RaycastHit hit, float distance, int mask)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true) return Physics.Raycast(ray, out hit, distance, mask);
            return SprayPaintRaycast.TryHit(ray.origin, ray.direction, distance, mask,
                Singleton<Gameplay>.i?.Player?.Get_ActiveTool?.transform, out hit);
        }

    }
}
