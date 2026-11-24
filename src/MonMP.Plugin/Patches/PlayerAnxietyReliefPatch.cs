using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NWH;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.Tick))]
    internal static class PlayerAnxietyReliefPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo speed = AccessTools.PropertyGetter(typeof(Vehicle), nameof(Vehicle.Speed));
            MethodInfo replacement = AccessTools.Method(typeof(PlayerAnxietyRelief), nameof(PlayerAnxietyRelief.VehicleSpeed));
            int sites = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(speed))
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = replacement; sites++;
                }
                yield return instruction;
            }
            if (sites != 1) throw new InvalidOperationException("Unsupported native vehicle anxiety speed read.");
        }

        private static void Postfix(Gameplay __instance) => PlayerAnxietyRelief.AddedPassengerTick(__instance);
    }
}
