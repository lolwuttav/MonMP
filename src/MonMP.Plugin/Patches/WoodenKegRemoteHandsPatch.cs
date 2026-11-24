using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Item_WoodenKeg), nameof(Item_WoodenKeg.Use))]
    internal static class WoodenKegRemoteHandsPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var getter = AccessTools.PropertyGetter(typeof(PlayerInteractable), nameof(PlayerInteractable.IsHoldingAnObject));
            bool IsHands(CodeInstruction instruction) => instruction.operand is MethodInfo method && method == getter;
            if (getter == null || code.Count(IsHands) != 1)
                throw new InvalidOperationException("Unsupported wooden-keg native hand precondition.");
            foreach (var instruction in code)
            {
                if (IsHands(instruction))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(WoodenKegRemoteHandsPatch), nameof(IsHolding));
                }
                yield return instruction;
            }
        }
        private static bool IsHolding(PlayerInteractable player) =>
            !SharedWoodenKegs.RemoteExtractionActive && player.IsHoldingAnObject;
    }
}
