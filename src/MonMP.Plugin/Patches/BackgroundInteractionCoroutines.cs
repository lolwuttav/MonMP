using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // These timers perform gameplay work and do not consume a rendered frame. Unity can
    // leave end-of-frame waits suspended for an occluded/background game window.
    [HarmonyPatch]
    internal static class BackgroundInteractionCoroutines
    {
        internal static IEnumerable<MethodInfo> Factories()
        {
            yield return Factory(typeof(Vehicle_IgnitionSwitch), "FirstContact");
            yield return Factory(typeof(Furniture_Phone), "Dialing");
            yield return Factory(typeof(Furniture_Phone), "Ringing");
            yield return Factory(typeof(AirCompressor), "MotorRunning");
            yield return Factory(typeof(NitrousRefillStation), "RefillBottle", typeof(Part_NitrousBottle));
            yield return Factory(typeof(SugarShack_CrimpingMachine), "Working", typeof(Item_MapleSyrupCan_Empty));
            yield return Factory(typeof(Furniture_Fryer), "BuildPoutine");
            yield return Factory(typeof(Bunker_CompostMachine), "BuildFertilizer");
            yield return Factory(typeof(Bunker_Bathtub), "OperatingBath");
        }

        private static MethodInfo Factory(Type type, string name, params Type[] arguments)
        {
            var factory = AccessTools.Method(type, name, arguments);
            if (factory == null || factory.IsStatic || factory.ReturnType != typeof(IEnumerator) ||
                !factory.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments))
                throw new InvalidOperationException("Unsupported native interaction coroutine: " + type.FullName + "." + name);
            return factory;
        }

        internal static IEnumerable<MethodInfo> Targets()
        {
            foreach (var factory in Factories())
            {
                var step = AccessTools.EnumeratorMoveNext(factory);
                if (step == null || step.IsStatic || step.ReturnType != typeof(bool) || step.GetParameters().Length != 0 ||
                    step.DeclaringType == null || !typeof(IEnumerator).IsAssignableFrom(step.DeclaringType))
                    throw new InvalidOperationException("Unsupported native interaction coroutine step: " + factory.DeclaringType?.FullName + "." + factory.Name);
                yield return step;
            }
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        // Patch the native step itself: Mono can inline the small iterator factory at
        // its caller before a factory postfix has a chance to wrap the returned iterator.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var nextFrame = AccessTools.Method(typeof(BackgroundInteractionCoroutines), nameof(NextGameplayFrame));
            bool found = false;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo constructor &&
                    constructor.DeclaringType == typeof(WaitForEndOfFrame) && constructor.GetParameters().Length == 0)
                {
                    // Mutate in place to retain the native branch labels and exception blocks.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = nextFrame;
                    found = true;
                }
                yield return instruction;
            }
            if (!found) throw new InvalidOperationException("Native gameplay timer has no end-of-frame yield: " + __originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name);
        }

        private static WaitForEndOfFrame? NextGameplayFrame() =>
            MonMPPlugin.Instance?.Session?.IsRunning == true ? null : new WaitForEndOfFrame();
    }
}
